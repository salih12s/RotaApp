using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure.Configurations;

internal sealed class DispatchJobConfiguration : IEntityTypeConfiguration<DispatchJob>
{
    public void Configure(EntityTypeBuilder<DispatchJob> builder)
    {
        builder.ToTable("dispatch_jobs");
        builder.HasKey(j => j.OrderId);
        builder.Property(j => j.OrderId).ValueGeneratedNever();

        // EF maps only properties with a setter by convention. Every get-only property is listed on
        // purpose, or it is silently left out of the table (F02 lesson: DistanceMeters).
        builder.Property(j => j.MerchantId);
        builder.Property(j => j.PickupLatitude).HasColumnName("pickup_lat");
        builder.Property(j => j.PickupLongitude).HasColumnName("pickup_lon");
        builder.Property(j => j.PickupAddress);
        builder.Property(j => j.DropoffLatitude).HasColumnName("dropoff_lat");
        builder.Property(j => j.DropoffLongitude).HasColumnName("dropoff_lon");
        builder.Property(j => j.DropoffAddress);
        builder.Property(j => j.SearchStartedAt);

        builder.Property(j => j.Status).HasConversion<string>();
        builder.Property(j => j.OfferAttempts).HasDefaultValue(0);

        // The search loop looks for due jobs only among those still searching.
        builder.HasIndex(j => j.NextSearchAt)
            .HasFilter("status = 'Searching'")
            .HasDatabaseName("ix_jobs_next_search");
    }
}
