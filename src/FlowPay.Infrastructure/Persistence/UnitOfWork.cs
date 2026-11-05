using System.Data;
using FlowPay.Application.Abstractions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;

namespace FlowPay.Infrastructure.Persistence;

/// <summary>EF Core implementation of the per-request transaction boundary.</summary>
public sealed class UnitOfWork : IUnitOfWork, IAsyncDisposable, IDisposable
{
    private readonly FlowPayDbContext _context;
    private readonly ILogger<UnitOfWork> _logger;
    private IDbContextTransaction? _transaction;

    public UnitOfWork(FlowPayDbContext context, ILogger<UnitOfWork> logger)
    {
        _context = context;
        _logger = logger;
    }

    public bool HasActiveTransaction => _transaction is not null;

    /// <summary>
    /// Opens a READ COMMITTED transaction.
    /// </summary>
    /// <remarks>
    /// READ COMMITTED, not a stricter level, and that choice is load-bearing. The
    /// idempotency claim blocks on the unique index when a competing request holds the
    /// same key, and once it unblocks it has to see that request's committed row —
    /// which a snapshot taken at transaction start would hide, leaving the loser of the
    /// race unable to find the response it should replay. Where stronger serialization is
    /// genuinely needed, it comes from explicit row locks, which cost only the rows
    /// involved instead of serializing unrelated traffic.
    /// </remarks>
    public async Task BeginAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is not null)
        {
            throw new InvalidOperationException("A transaction is already active.");
        }

        _transaction = await _context.Database
            .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        => _context.SaveChangesAsync(cancellationToken);

    public async Task CommitAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            throw new InvalidOperationException("There is no active transaction to commit.");
        }

        await _context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await _transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

        await DisposeTransactionAsync().ConfigureAwait(false);
    }

    public async Task RollbackAsync(CancellationToken cancellationToken = default)
    {
        if (_transaction is null)
        {
            return;
        }

        try
        {
            await _transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception failure)
        {
            // The connection may already be in an unusable state, in which case the
            // server has rolled back for us. Nothing is lost by proceeding.
            _logger.LogDebug(failure, "Rollback raised; the transaction was already unwound.");
        }
        finally
        {
            // Pending tracked changes belong to the transaction that just died. Leaving
            // them would mean a later SaveChanges on this scoped context tried to write
            // state that was deliberately abandoned.
            _context.ChangeTracker.Clear();
            await DisposeTransactionAsync().ConfigureAwait(false);
        }
    }

    public async ValueTask DisposeAsync() => await DisposeTransactionAsync().ConfigureAwait(false);

    /// <summary>
    /// Synchronous disposal, for the DI container.
    /// </summary>
    /// <remarks>
    /// Implementing only <see cref="IAsyncDisposable"/> is not enough: a scoped service
    /// is disposed by whatever disposes its scope, and a synchronous
    /// <c>using (scope)</c> anywhere in the codebase would throw at runtime rather than
    /// at compile time. Supporting both protocols removes that trap.
    /// </remarks>
    public void Dispose()
    {
        _transaction?.Dispose();
        _transaction = null;
    }

    private async ValueTask DisposeTransactionAsync()
    {
        if (_transaction is not null)
        {
            await _transaction.DisposeAsync().ConfigureAwait(false);
            _transaction = null;
        }
    }
}
