using System.Text;
using Temporalio.Client;

namespace TxGuard.Api;

/// <summary>
/// Applies transport security to the Temporal client connection from configuration,
/// so the same code base talks to a plaintext local server in development and to a
/// TLS-secured Temporal Cloud namespace in production.
///
/// Two mutually exclusive auth modes are supported (both require TLS):
///   • API key    — set <c>Temporal:ApiKey</c>. Namespace must be <c>namespace.accountId</c>
///                  and the host a regional gRPC endpoint (e.g. <c>us-east-1.aws.api.temporal.io:7233</c>).
///   • mTLS certs — set <c>Temporal:Tls:Enabled=true</c> plus a client cert + private key.
///                  Host is <c>namespace.accountId.tmprl.cloud:7233</c>.
///
/// When neither is configured the connection stays plaintext (local dev default).
/// Certificates may be supplied either as a file path (Render "Secret Files", mounted
/// Kubernetes secrets, …) or inline PEM text (env var) — path wins when both are set.
/// </summary>
public static class TemporalConnectionConfig
{
    public static void Apply(TemporalClientConnectOptions options, IConfiguration config)
    {
        var apiKey = config["Temporal:ApiKey"];
        var tls = config.GetSection("Temporal:Tls");
        var tlsEnabled = tls.GetValue("Enabled", false);

        // API-key auth: TLS on, no client cert needed. Takes precedence if provided.
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            options.ApiKey = apiKey;
            options.Tls = new TlsOptions();
            return;
        }

        // Plaintext local development.
        if (!tlsEnabled)
        {
            return;
        }

        var tlsOptions = new TlsOptions();

        var clientCert = ReadPem(tls["ClientCertPath"], tls["ClientCertPem"]);
        var clientKey = ReadPem(tls["ClientKeyPath"], tls["ClientKeyPem"]);
        if (clientCert is not null && clientKey is not null)
        {
            tlsOptions.ClientCert = clientCert;
            tlsOptions.ClientPrivateKey = clientKey;
        }
        else if (clientCert is not null || clientKey is not null)
        {
            throw new InvalidOperationException(
                "Temporal mTLS requires BOTH Temporal:Tls:ClientCert* and Temporal:Tls:ClientKey* to be set.");
        }

        var serverCa = ReadPem(tls["ServerCaPath"], tls["ServerCaPem"]);
        if (serverCa is not null)
        {
            tlsOptions.ServerRootCACert = serverCa;
        }

        // Optional SNI / certificate-name override (rarely needed with Temporal Cloud).
        var serverName = tls["ServerName"];
        if (!string.IsNullOrWhiteSpace(serverName))
        {
            tlsOptions.Domain = serverName;
        }

        options.Tls = tlsOptions;
    }

    /// <summary>
    /// Loads PEM bytes from a file path when given, otherwise from inline PEM text.
    /// Returns null when neither is provided.
    /// </summary>
    private static byte[]? ReadPem(string? path, string? inlinePem)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            return File.ReadAllBytes(path);
        }

        if (!string.IsNullOrWhiteSpace(inlinePem))
        {
            return Encoding.UTF8.GetBytes(inlinePem);
        }

        return null;
    }
}
