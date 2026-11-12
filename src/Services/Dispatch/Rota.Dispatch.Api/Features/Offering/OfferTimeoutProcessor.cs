using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Offering;

/// <summary>
/// Closes offers nobody answered in time. Several Dispatch instances run this at once; <c>FOR UPDATE SKIP LOCKED</c>
/// makes each take different rows: a row another worker has locked is skipped, not waited for, so every expired
/// offer is processed exactly once and no worker idles behind another.
/// </summary>
public sealed class OfferTimeoutProcessor(DispatchDbContext db, OfferNotifier notifier, IOptions<DispatchOptions> options, TimeProvider time)
{
    /// <summary>Handles up to <paramref name="batchSize"/> due offers in one transaction and returns how many it expired.</summary>
    public async Task<int> ProcessDueAsync(int batchSize, CancellationToken cancellationToken)
    {
        var rules = options.Value.Rules;
        var now = time.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Lock order starts with the offer rows (see DispatchSql): they are locked here, the job and courier follow.
        var due = await db.Offers.FromSql($"""
                SELECT * FROM offers
                WHERE status = 'Pending' AND expires_at <= {now}
                ORDER BY expires_at
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var expired = 0;
        foreach (var offer in due)
        {
            // Same condition as the claim: if the answer won the race, this finds 0 rows and we move on.
            var closed = await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE offers SET status = 'Expired'
                WHERE id = {offer.Id} AND status = 'Pending' AND expires_at <= {now}
                """, cancellationToken);
            if (closed == 0)
            {
                continue;
            }

            await db.CloseDeclinedOfferAsync(offer, rules, now, cancellationToken);

            // The courier's stream shows "withdrawn" for an offer that is no longer pending.
            db.AfterCommit(_ => notifier.NotifyAsync(offer.CourierId, offer.Id));
            expired++;
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return expired;
    }
}
