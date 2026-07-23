using System.Text;
using System.Text.Json.Serialization;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Temporalio.Extensions.Hosting;
using TxGuard.Api;
using TxGuard.Api.Auth;
using TxGuard.Api.Contracts;
using TxGuard.Api.Demo;
using TxGuard.Api.Realtime;
using TxGuard.Domain.Abstractions;
using TxGuard.Infrastructure;
using TxGuard.Infrastructure.Persistence;
using TxGuard.Workflows;

var builder = WebApplication.CreateBuilder(args);

// Honor the platform-assigned port (Render, Railway, Fly, … all inject PORT). Locally
// this is unset, so launchSettings/ASPNETCORE_URLS keep working unchanged.
var port = Environment.GetEnvironmentVariable("PORT");
if (!string.IsNullOrWhiteSpace(port))
{
    builder.WebHost.UseUrls($"http://0.0.0.0:{port}");
}

var temporalHost = builder.Configuration["Temporal:Host"] ?? "localhost:7233";
var temporalNamespace = builder.Configuration["Temporal:Namespace"] ?? "default";
const string CorsPolicy = "spa";

// ── Services ──────────────────────────────────────────────────────────────
builder.Services.AddTxGuardInfrastructure(builder.Configuration);

// Real-time updates: replace the default no-op notifier with the SignalR one.
builder.Services.AddSignalR();
builder.Services.AddSingleton<ITransactionNotifier, SignalRTransactionNotifier>();

// Temporal client (used by the API to start workflows and send signals). Configured via
// the options callback so TemporalConnectionConfig can layer on TLS / API-key auth from
// configuration — the same registration reaches a plaintext local server or a TLS
// Temporal Cloud namespace.
builder.Services.AddTemporalClient(options =>
{
    options.TargetHost = temporalHost;
    options.Namespace = temporalNamespace;
    TemporalConnectionConfig.Apply(options, builder.Configuration);
});

// In-process Temporal worker hosting the workflow + activities on the task queue.
// Registered through ControllableWorkerHost (rather than AddHostedTemporalWorker) so
// the demo panel can stop and restart it to show durable queueing + resumption.
builder.Services.AddSingleton<ControllableWorkerHost>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<ControllableWorkerHost>());

// Demo/chaos controls — the controller itself is gated to Development.
builder.Services.AddSingleton<DbChaosService>();

// ── Authentication (JWT bearer + API key) + RBAC ────────────────────────────
builder.Services.Configure<AuthOptions>(builder.Configuration.GetSection(AuthOptions.SectionName));
builder.Services.AddSingleton<UserStore>();
builder.Services.AddSingleton<TokenService>();
builder.Services.AddSingleton<ApiKeyService>();

const string JwtOrApiKey = "JwtOrApiKey";
var authOptions = builder.Configuration.GetSection(AuthOptions.SectionName).Get<AuthOptions>() ?? new AuthOptions();

// Fail fast rather than ship the dev signing key to a real environment.
if (builder.Environment.IsProduction() &&
    authOptions.Jwt.SigningKey.Contains("dev-only", StringComparison.OrdinalIgnoreCase))
{
    throw new InvalidOperationException(
        "Production start blocked: set a real Auth__Jwt__SigningKey (>= 32 bytes). The bundled dev key is insecure.");
}
builder.Services
    .AddAuthentication(o =>
    {
        // A policy scheme picks the real scheme per request: X-Api-Key header → API key,
        // otherwise JWT bearer. Keeps [Authorize]/[Authorize(Roles=…)] working for both.
        o.DefaultScheme = JwtOrApiKey;
        o.DefaultChallengeScheme = JwtOrApiKey;
    })
    .AddPolicyScheme(JwtOrApiKey, JwtOrApiKey, o =>
    {
        o.ForwardDefaultSelector = ctx =>
            ctx.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                ? ApiKeyAuthenticationHandler.SchemeName
                : JwtBearerDefaults.AuthenticationScheme;
    })
    .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(
        ApiKeyAuthenticationHandler.SchemeName, null)
    .AddJwtBearer(o =>
    {
        o.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = authOptions.Jwt.Issuer,
            ValidAudience = authOptions.Jwt.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.Jwt.SigningKey)),
            ClockSkew = TimeSpan.FromSeconds(30),
        };

        // SignalR (WebSockets) can't send an Authorization header, so accept the token
        // from the query string on hub connections.
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = ctx =>
            {
                var accessToken = ctx.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    ctx.HttpContext.Request.Path.StartsWithSegments("/hubs"))
                    ctx.Token = accessToken;
                return Task.CompletedTask;
            },
        };
    });
builder.Services.AddAuthorization();

// Accept/emit enums as strings (e.g. "Transfer", "Completed") across the API.
builder.Services
    .AddControllers()
    .AddJsonOptions(o =>
        o.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));

// Emit the standardised ApiError shape (FR-SQ-005) for [ApiController] model-validation
// failures, instead of the default ValidationProblemDetails, so the error contract is
// uniform with the handlers' own BadRequest(new ApiError(...)) responses.
builder.Services.Configure<ApiBehaviorOptions>(o =>
    o.InvalidModelStateResponseFactory = ctx =>
    {
        var message = string.Join("; ", ctx.ModelState
            .Where(kv => kv.Value?.Errors.Count > 0)
            .Select(kv => $"{kv.Key}: {kv.Value!.Errors[0].ErrorMessage}"));
        if (string.IsNullOrEmpty(message))
            message = "One or more validation errors occurred";
        return new BadRequestObjectResult(new ApiError("TXG-INVALID", message));
    });

// Allowed browser origins come from config (Cors:AllowedOrigins) so the deployed
// Vercel URL can be added without a rebuild; falls back to the local Vite dev/preview
// ports. Set via env: Cors__AllowedOrigins__0=https://your-app.vercel.app
var corsOrigins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>();
if (corsOrigins is null || corsOrigins.Length == 0)
{
    corsOrigins = ["http://localhost:5173", "http://localhost:4173"];
}
builder.Services.AddCors(o => o.AddPolicy(CorsPolicy, p => p
    .WithOrigins(corsOrigins)
    .AllowAnyHeader().AllowAnyMethod().AllowCredentials()));

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// ── Reverse-proxy awareness ───────────────────────────────────────────────
// Caddy terminates TLS and forwards over the private Docker network, so without this
// every request appears to come from the proxy's IP — which would make the per-IP rate
// limiter below partition everything into a single bucket. Clearing KnownProxies is safe
// here precisely because the API is not reachable except through Caddy.
builder.Services.Configure<ForwardedHeadersOptions>(o =>
{
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    o.KnownNetworks.Clear();
    o.KnownProxies.Clear();
});

// ── Rate limiting ─────────────────────────────────────────────────────────
// The service is internet-facing and already being probed by scanners. A global per-IP
// budget blunts scraping, and a much tighter budget on login makes credential stuffing
// against the demo accounts impractical.
const string AuthRateLimit = "auth";
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    o.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 120,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));

    o.AddPolicy(AuthRateLimit, ctx =>
        RateLimitPartition.GetFixedWindowLimiter(
            ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
            }));
});

var app = builder.Build();

// ── Ensure the read-model schema exists (dev convenience) ─────────────────
using (var scope = app.Services.CreateScope())
{
    var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<TxGuardDbContext>>();
    await using var db = await dbf.CreateDbContextAsync();
    await db.Database.EnsureCreatedAsync();

    // EnsureCreated does not add tables to an already-existing database, so create the
    // api_keys table idempotently for DBs provisioned before this feature existed.
    await db.Database.ExecuteSqlRawAsync("""
        CREATE TABLE IF NOT EXISTS api_keys (
            "Id"            bigint GENERATED BY DEFAULT AS IDENTITY PRIMARY KEY,
            "Name"          varchar(128) NOT NULL,
            "Prefix"        varchar(32)  NOT NULL,
            "Hash"          varchar(64)  NOT NULL,
            "Role"          varchar(32)  NOT NULL,
            "CreatedBy"     varchar(128) NOT NULL,
            "CreatedAtUtc"  timestamptz  NOT NULL,
            "LastUsedAtUtc" timestamptz  NULL,
            "RevokedAtUtc"  timestamptz  NULL
        );
        CREATE UNIQUE INDEX IF NOT EXISTS "IX_api_keys_Hash" ON api_keys ("Hash");
        """);
}

// ── Pipeline ──────────────────────────────────────────────────────────────
app.UseForwardedHeaders();

// Swagger publishes the full API surface, including the admin endpoints. That is useful
// locally and an unnecessary disclosure on a public host, so it is gated out of Production.
if (!app.Environment.IsProduction())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseCors(CorsPolicy);

app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllers();
app.MapHub<TransactionsHub>("/hubs/transactions").RequireAuthorization();
app.MapGet("/health", () => Results.Ok(new { status = "ok", service = "TxGuard" }));

app.Run();
