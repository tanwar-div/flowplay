using System.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace FlowPay.Infrastructure.Persistence;

/// <summary>
/// Creates ADO commands that share the context's connection and ambient transaction.
/// </summary>
/// <remarks>
/// A handful of operations are expressed in SQL rather than LINQ, and they fall into two
/// groups. The lock-and-claim statements (<c>FOR UPDATE</c>, <c>FOR UPDATE SKIP LOCKED</c>,
/// <c>ON CONFLICT DO NOTHING</c>) have no LINQ equivalent and are the system's
/// concurrency primitives. The reconciliation aggregates sum the entire ledger, which
/// belongs in the database rather than streamed into the process to be added up. Both
/// must run on the caller's transaction, which is what this helper guarantees.
/// </remarks>
internal static class RawSql
{
    public static async Task<NpgsqlCommand> CreateCommandAsync(
        this DbContext context,
        string sql,
        CancellationToken cancellationToken)
    {
        NpgsqlConnection connection = (NpgsqlConnection)context.Database.GetDbConnection();

        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }

        NpgsqlCommand command = connection.CreateCommand();
        command.CommandText = sql;

        if (context.Database.CurrentTransaction is { } transaction)
        {
            command.Transaction = (NpgsqlTransaction)transaction.GetDbTransaction();
        }

        return command;
    }
}
