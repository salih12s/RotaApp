using System.Linq.Expressions;
using Rota.Dispatch.Api.Domain;
using Rota.Dispatch.Api.Infrastructure;

namespace Rota.Dispatch.Api.Features.Offering;

/// <summary>An offer with the places the courier needs to see (from the job), as sent over REST and gRPC.</summary>
public sealed record OfferView(
    Guid OfferId,
    Guid OrderId,
    Guid CourierId,
    OfferStatus Status,
    JobPlace Pickup,
    JobPlace Dropoff,
    int DistanceMeters,
    int EtaSeconds,
    DateTimeOffset ExpiresAt);

public static class OfferQueries
{
    /// <summary>
    /// The offers matching <paramref name="filter"/>, joined with their job. The filter is on <see cref="Offer"/>, not on the view:
    /// EF Core cannot translate a condition on a constructor-built projection, so it has to be applied before the projection.
    /// </summary>
    public static IQueryable<OfferView> OfferViews(this DispatchDbContext db, Expression<Func<Offer, bool>> filter)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(filter);

        return from offer in db.Offers.Where(filter)
               join job in db.DispatchJobs on offer.OrderId equals job.OrderId
               select new OfferView(
                   offer.Id,
                   offer.OrderId,
                   offer.CourierId,
                   offer.Status,
                   new JobPlace(job.PickupLatitude, job.PickupLongitude, job.PickupAddress),
                   new JobPlace(job.DropoffLatitude, job.DropoffLongitude, job.DropoffAddress),
                   offer.DistanceMeters,
                   offer.EtaSeconds,
                   offer.ExpiresAt);
    }
}
