using Microsoft.EntityFrameworkCore;

namespace Rota.Tracking.Api.Infrastructure;

/// <summary>
/// The Tracking database (<c>rota_tracking</c>). It maps no entities on purpose: EF Core cannot create a
/// partitioned table from a model, and the hot path writes with <c>COPY</c>, not with EF. The context
/// exists to carry the migrations (raw SQL), so the schema is built the same way as in Orders and Dispatch.
/// </summary>
public sealed class TrackingDbContext(DbContextOptions<TrackingDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Tracking";
}
