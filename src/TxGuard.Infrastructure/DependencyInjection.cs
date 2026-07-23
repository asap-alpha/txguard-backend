using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TxGuard.Domain.Abstractions;
using TxGuard.Infrastructure.Banking;
using TxGuard.Infrastructure.Configuration;
using TxGuard.Infrastructure.Fraud;
using TxGuard.Infrastructure.Persistence;

namespace TxGuard.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddTxGuardInfrastructure(
        this IServiceCollection services, IConfiguration config)
    {
        services.Configure<TxGuardOptions>(config.GetSection(TxGuardOptions.SectionName));

        // Live tuning knobs, seeded from configuration; the demo panel mutates these.
        services.AddSingleton<IRuntimeSettings, RuntimeSettings>();

        var connectionString = NormalizePostgresConnectionString(
            config.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5433;Database=txguard;Username=txguard;Password=txguard");

        // DbContextFactory: safe to resolve per-operation from Temporal activities.
        services.AddDbContextFactory<TxGuardDbContext>(o => o.UseNpgsql(connectionString));

        // Pluggable ports — swap these registrations to change implementations.
        services.AddSingleton<IBankingAdapter, MockBankingAdapter>();
        services.AddSingleton<IFraudScorer, HeuristicFraudScorer>();
        services.AddScoped<ITransactionStore, EfTransactionStore>();

        // Default no-op notifier; the API replaces this with a SignalR-backed one.
        services.AddSingleton<ITransactionNotifier, NullTransactionNotifier>();

        return services;
    }

    /// <summary>
    /// Accepts either Npgsql keyword form (Host=…;Port=…) or a URI
    /// (postgres://user:pass@host:port/db?params) as handed out by managed providers
    /// such as Render (<c>fromDatabase</c>) and Aiven, converting the latter to keyword
    /// form. For any non-local host, SSL is required (Aiven/Render reject plaintext), so
    /// SSL Mode=Require + Trust Server Certificate are added when not already specified.
    /// </summary>
    /// <summary>Npgsql's out-of-the-box Maximum Pool Size, used to detect "caller didn't set one".</summary>
    private const int DefaultNpgsqlMaxPoolSize = 100;

    public static string NormalizePostgresConnectionString(string connectionString)
    {
        var isUri = connectionString.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
                    || connectionString.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase);

        var builder = new Npgsql.NpgsqlConnectionStringBuilder();

        if (isUri)
        {
            var uri = new Uri(connectionString);
            var userInfo = uri.UserInfo.Split(':', 2);

            builder.Host = uri.Host;
            builder.Port = uri.Port > 0 ? uri.Port : 5432;
            builder.Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/'));
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
            if (userInfo.Length > 1)
            {
                builder.Password = Uri.UnescapeDataString(userInfo[1]);
            }

            // Carry over an explicit sslmode from the query string if present.
            var query = uri.Query.TrimStart('?');
            foreach (var pair in query.Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0].Equals("sslmode", StringComparison.OrdinalIgnoreCase))
                {
                    builder.SslMode = Enum.Parse<Npgsql.SslMode>(kv[1], ignoreCase: true);
                }
            }
        }
        else
        {
            builder.ConnectionString = connectionString;
        }

        // Managed Postgres requires TLS; only local loopback stays plaintext by default.
        // A caller-supplied sslmode (any value other than the Prefer default) is respected.
        // Require encrypts without validating the server chain, so Aiven's private CA needs
        // no extra trust configuration; use VerifyCA/VerifyFull + a root cert to pin it.
        var isLocal = string.IsNullOrEmpty(builder.Host)
                      || builder.Host is "localhost" or "127.0.0.1" or "::1";
        if (!isLocal && builder.SslMode == Npgsql.SslMode.Prefer)
        {
            builder.SslMode = Npgsql.SslMode.Require;
        }

        // Free-tier managed Postgres (Aiven free-1-5gb especially) caps max_connections at a
        // couple of dozen, while Npgsql's default pool is 100. The in-process Temporal worker
        // opens a DbContext per activity, so an uncapped pool exhausts the server's slots and
        // fails with 53300. Cap remote pools unless the connection string sets its own size.
        if (!isLocal && builder.MaxPoolSize == DefaultNpgsqlMaxPoolSize)
        {
            builder.MaxPoolSize = 10;
        }

        return builder.ConnectionString;
    }
}
