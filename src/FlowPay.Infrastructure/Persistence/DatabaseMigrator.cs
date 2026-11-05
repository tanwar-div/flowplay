using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace FlowPay.Infrastructure.Persistence;

/// <summary>
/// Applies the embedded schema scripts to bring a database up to date.
/// </summary>
/// <remarks>
/// <para>
/// Hand-written SQL applied by a small runner, rather than generated migrations. The
/// schema's integrity constraints — the over-refund check, the non-negative balance
/// check, the partial index the outbox dispatcher depends on, the generated signed-amount
/// column — are the point of the schema, and writing them directly keeps them legible and
/// reviewable instead of buried in generated code.
/// </para>
/// <para>
/// Startup is a natural race: several API and worker instances can come up at once
/// against a fresh database. A session-level advisory lock serializes them, so exactly one
/// instance applies a given script and the others wait and then find it already recorded.
/// </para>
/// </remarks>
public sealed partial class DatabaseMigrator
{
    // An arbitrary but fixed key. Any process that might apply migrations uses this same
    // value, which is what makes the lock meaningful.
    private const long AdvisoryLockKey = 0x_F107_A1C0_DE;

    private readonly string _connectionString;
    private readonly ILogger<DatabaseMigrator> _logger;

    public DatabaseMigrator(string connectionString, ILogger<DatabaseMigrator> logger)
    {
        _connectionString = connectionString;
        _logger = logger;
    }

    /// <summary>Applies every script that has not yet been recorded as applied.</summary>
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using NpgsqlConnection connection = new(_connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            """
            CREATE TABLE IF NOT EXISTS schema_migrations (
                script_name varchar(200)  NOT NULL PRIMARY KEY,
                applied_at  timestamptz   NOT NULL DEFAULT now(),
                checksum    varchar(64)   NOT NULL
            );
            """,
            cancellationToken).ConfigureAwait(false);

        await ExecuteAsync(
            connection,
            $"SELECT pg_advisory_lock({AdvisoryLockKey});",
            cancellationToken).ConfigureAwait(false);

        try
        {
            HashSet<string> applied = await LoadAppliedScriptsAsync(connection, cancellationToken)
                .ConfigureAwait(false);

            foreach ((string name, string sql) in LoadScripts())
            {
                if (applied.Contains(name))
                {
                    continue;
                }

                _logger.LogInformation("Applying schema script {ScriptName}.", name);

                // Script and bookkeeping row commit together, so a crash mid-script can
                // never leave a half-applied migration recorded as done.
                await using NpgsqlTransaction transaction = await connection
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                await using (NpgsqlCommand command = new(sql, connection, transaction))
                {
                    await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await using (NpgsqlCommand record = new(
                    "INSERT INTO schema_migrations (script_name, checksum) VALUES (@name, @checksum);",
                    connection,
                    transaction))
                {
                    record.Parameters.AddWithValue("name", name);
                    record.Parameters.AddWithValue("checksum", Checksum(sql));
                    await record.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
                }

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        finally
        {
            await ExecuteAsync(
                connection,
                $"SELECT pg_advisory_unlock({AdvisoryLockKey});",
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static async Task<HashSet<string>> LoadAppliedScriptsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        HashSet<string> applied = new(StringComparer.Ordinal);

        await using NpgsqlCommand command =
            new("SELECT script_name FROM schema_migrations;", connection);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        return applied;
    }

    /// <summary>
    /// Reads the embedded scripts in name order, which is why they are numbered.
    /// </summary>
    private static IEnumerable<(string Name, string Sql)> LoadScripts()
    {
        Assembly assembly = typeof(DatabaseMigrator).Assembly;

        IEnumerable<string> names = assembly
            .GetManifestResourceNames()
            .Where(static name => name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
            .OrderBy(static name => name, StringComparer.Ordinal);

        foreach (string resourceName in names)
        {
            using Stream? stream = assembly.GetManifestResourceStream(resourceName);

            if (stream is null)
            {
                continue;
            }

            using StreamReader reader = new(stream);
            yield return (ShortName(resourceName), reader.ReadToEnd());
        }
    }

    private static string ShortName(string resourceName)
    {
        Match match = ScriptNamePattern().Match(resourceName);
        return match.Success ? match.Groups[1].Value : resourceName;
    }

    private static string Checksum(string sql)
        => Convert.ToHexStringLower(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(sql)))[..32];

    private static async Task ExecuteAsync(
        NpgsqlConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using NpgsqlCommand command = new(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    [GeneratedRegex(@"([^.]+\.sql)$", RegexOptions.CultureInvariant)]
    private static partial Regex ScriptNamePattern();
}
