using FlowPay.Application.Abstractions;
using FlowPay.Domain.Payments;
using FlowPay.Domain.Primitives;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace FlowPay.Infrastructure.Persistence;

/// <inheritdoc />
public sealed class AccountRepository : IAccountRepository
{
    private readonly FlowPayDbContext _context;

    public AccountRepository(FlowPayDbContext context) => _context = context;

    public Task<Account?> FindAsync(AccountId id, CancellationToken cancellationToken = default)
        => _context.Accounts
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

    /// <inheritdoc />
    public Task<Account?> LockForUpdateAsync(
        AccountId id,
        CancellationToken cancellationToken = default)
        // FromSql keeps the row in EF's change tracker, so the domain can mutate the
        // returned aggregate and have the UPDATE flushed on commit. Dropping to raw ADO
        // here would mean hand-writing an UPDATE for every balance change.
        //
        // xmin is projected explicitly because it is a PostgreSQL system column that
        // `SELECT *` omits, and EF wraps this query as a subquery before selecting the
        // mapped columns — including the concurrency token — from it.
        => _context.Accounts
            .FromSql($"SELECT *, xmin FROM accounts WHERE id = {id.Value} FOR UPDATE")
            .AsTracking()
            .FirstOrDefaultAsync(cancellationToken);

    /// <inheritdoc />
    public async Task<AccountLockSet> LockForUpdateAsync(
        IReadOnlyList<AccountId> ids,
        CancellationToken cancellationToken = default)
    {
        // Sorted, then locked one row at a time in that order.
        //
        // A single `WHERE id = ANY(...) ORDER BY id FOR UPDATE` would be one round trip
        // instead of two or three, but PostgreSQL does not guarantee it acquires the row
        // locks in the order the sort produced, so two transactions touching the same
        // pair of accounts could still deadlock and need retrying. Locking sequentially
        // in a globally consistent order makes a wait cycle impossible rather than merely
        // recoverable. The extra round trips are sub-millisecond and happen at most three
        // times per payment, which is a fair price for not having deadlocks under load.
        Guid[] ordered = new Guid[ids.Count];
        for (int i = 0; i < ids.Count; i++)
        {
            ordered[i] = ids[i].Value;
        }

        Array.Sort(ordered);

        List<Account> locked = new(ordered.Length);
        Guid previous = Guid.Empty;

        foreach (Guid id in ordered)
        {
            // Callers can legitimately name the same account twice — a merchant that is
            // also the fee account, say — and locking it again would be wasted work.
            if (id == previous)
            {
                continue;
            }

            previous = id;

            Account? account = await _context.Accounts
                .FromSql($"SELECT *, xmin FROM accounts WHERE id = {id} FOR UPDATE")
                .AsTracking()
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);

            if (account is not null)
            {
                locked.Add(account);
            }
        }

        return new AccountLockSet(locked.ToArray());
    }

    public void Add(Account account) => _context.Accounts.Add(account);

    /// <inheritdoc />
    public async Task<IReadOnlyList<BalanceDiscrepancy>> FindBalanceDiscrepanciesAsync(
        CancellationToken cancellationToken = default)
    {
        // The reconciliation invariant: an account's current balance must equal the
        // balance it was opened with plus the net of every ledger line touching it. Any
        // balance mutation that happened without a matching ledger line — or any ledger
        // line written without the corresponding balance update — shows up here.
        //
        // Reserved funds are deliberately excluded: a reservation moves nothing, so it
        // must not appear in the ledger and must not affect this equation.
        const string Sql =
            """
            SELECT a.id,
                   a.balance_minor_units,
                   a.opening_balance_minor_units + COALESCE(l.net_minor_units, 0)
            FROM accounts a
            LEFT JOIN (
                SELECT account_id, SUM(signed_minor_units) AS net_minor_units
                FROM ledger_entries
                GROUP BY account_id
            ) l ON l.account_id = a.id
            WHERE a.balance_minor_units
                  <> a.opening_balance_minor_units + COALESCE(l.net_minor_units, 0)
            """;

        List<BalanceDiscrepancy> discrepancies = [];

        await using NpgsqlCommand command = await _context
            .CreateCommandAsync(Sql, cancellationToken)
            .ConfigureAwait(false);

        await using NpgsqlDataReader reader = await command
            .ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);

        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            discrepancies.Add(new BalanceDiscrepancy(
                new AccountId(reader.GetGuid(0)),
                reader.GetInt64(1),
                reader.GetInt64(2)));
        }

        return discrepancies;
    }
}
