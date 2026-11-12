using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>
/// Runs the actions queued with <see cref="DispatchDbContext.AfterCommit"/> once the database transaction
/// has really committed. The message consumer commits after its handler returns, so the handler itself
/// cannot know "after commit"; this hook can.
/// </summary>
public sealed class AfterCommitInterceptor : DbTransactionInterceptor
{
    public override async Task TransactionCommittedAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is DispatchDbContext db)
        {
            await db.RunAfterCommitAsync(cancellationToken);
        }
    }

    public override Task TransactionRolledBackAsync(
        DbTransaction transaction, TransactionEndEventData eventData, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(eventData);

        if (eventData.Context is DispatchDbContext db)
        {
            db.DiscardAfterCommit();
        }

        return Task.CompletedTask;
    }
}
