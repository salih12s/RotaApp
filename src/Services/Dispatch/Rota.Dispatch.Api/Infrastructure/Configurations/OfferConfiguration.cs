using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure.Configurations;

internal sealed class OfferConfiguration : IEntityTypeConfiguration<Offer>
{
    public void Configure(EntityTypeBuilder<Offer> builder)
    {
        builder.ToTable("offers");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        // Get-only properties are listed on purpose: EF skips them by convention (F02 lesson).
        builder.Property(o => o.OrderId);
        builder.Property(o => o.CourierId);
        builder.Property(o => o.DistanceMeters).HasColumnName("distance_m");
        builder.Property(o => o.EtaSeconds);
        builder.Property(o => o.CreatedAt);
        builder.Property(o => o.ExpiresAt);

        builder.Property(o => o.Status).HasConversion<string>();

        builder.HasOne<DispatchJob>().WithMany().HasForeignKey(o => o.OrderId);
        builder.HasOne<Courier>().WithMany().HasForeignKey(o => o.CourierId);

        // The timeout job scans only offers still waiting for an answer, so the index stays small.
        builder.HasIndex(o => o.ExpiresAt)
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ix_offers_pending_expiry");

        // Invariants I-1 and I-2 in the database: even buggy application code cannot create a second
        // pending offer for one order or one courier.
        builder.HasIndex(o => o.OrderId)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ux_offers_one_pending_per_order");
        builder.HasIndex(o => o.CourierId)
            .IsUnique()
            .HasFilter("status = 'Pending'")
            .HasDatabaseName("ux_offers_one_pending_per_courier");
    }
}
