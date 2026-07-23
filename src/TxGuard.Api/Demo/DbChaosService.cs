using Npgsql;
using TxGuard.Infrastructure;

namespace TxGuard.Api.Demo;

/// <summary>
/// Injects and heals an application-database outage for demos, by flipping the
/// <c>txguard</c> database's connection limit and terminating live sessions.
///
/// It deliberately does NOT shell out to `docker exec` — that would require the
/// Docker CLI and socket inside the API process (a privilege-escalation vector, and
/// broken as soon as the API itself is containerised). Instead it connects to the
/// server's <c>postgres</c> maintenance database and issues the DDL directly, which
/// leaves Temporal's own databases on the same server untouched.
/// </summary>
public sealed class DbChaosService
{
    private readonly string _maintenanceConnectionString;
    private readonly string _appDatabase;

    public DbChaosService(IConfiguration config)
    {
        // Normalize first: in production the configured string is Aiven's postgres:// URI,
        // which NpgsqlConnectionStringBuilder cannot parse directly (it expects keyword form).
        var appConnectionString = DependencyInjection.NormalizePostgresConnectionString(
            config.GetConnectionString("Postgres")
            ?? "Host=localhost;Port=5433;Database=txguard;Username=txguard;Password=txguard");

        var builder = new NpgsqlConnectionStringBuilder(appConnectionString);
        _appDatabase = builder.Database ?? "txguard";

        // Same server, different database — so we stay connected while the app DB is sealed
        // off. Locally that's the always-present "postgres" maintenance DB; managed providers
        // may not have one (Aiven has no "postgres" DB), so the name is configurable. Point it
        // at any small database on the same server that is NOT the application database.
        builder.Database = config["TxGuard:Demo:MaintenanceDatabase"] ?? "postgres";
        _maintenanceConnectionString = builder.ConnectionString;
    }

    /// <summary>True when the application database is currently refusing connections.</summary>
    public async Task<bool> IsBrokenAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            "select datconnlimit from pg_database where datname = @db", conn);
        cmd.Parameters.AddWithValue("db", _appDatabase);
        var result = await cmd.ExecuteScalarAsync(ct);
        return result is int limit && limit == 0;
    }

    /// <summary>Seals the app database: no new connections, existing ones terminated.</summary>
    public async Task BreakAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync(ct);

        // Connection limit 0 is the actual seal — it blocks all new connections, so the
        // pool can't reconnect and the app database is effectively down.
        await using (var limit = new NpgsqlCommand(
            $"alter database \"{_appDatabase}\" connection limit 0", conn))
            await limit.ExecuteNonQueryAsync(ct);

        // Then drop existing connections so the outage is immediate. Restrict to our own
        // role: a non-superuser (e.g. Aiven's avnadmin) may not terminate the provider's
        // internal backends, and trying would raise 42501 and abort the break. The app
        // connects as this same role, so this still severs every application connection.
        await using var kill = new NpgsqlCommand(
            "select pg_terminate_backend(pid) from pg_stat_activity " +
            "where datname = @db and pid <> pg_backend_pid() and usename = current_user", conn);
        kill.Parameters.AddWithValue("db", _appDatabase);
        await kill.ExecuteNonQueryAsync(ct);
    }

    /// <summary>Restores normal connectivity.</summary>
    public async Task HealAsync(CancellationToken ct = default)
    {
        await using var conn = new NpgsqlConnection(_maintenanceConnectionString);
        await conn.OpenAsync(ct);
        await using var cmd = new NpgsqlCommand(
            $"alter database \"{_appDatabase}\" connection limit -1", conn);
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
