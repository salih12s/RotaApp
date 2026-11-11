using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.Orders.Api.Domain;

namespace Rota.Orders.Api.Infrastructure.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public const string MerchantOrderRefIndex = "ux_orders_merchant_order_ref";

    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).ValueGeneratedNever();

        // EF maps only properties with a setter by convention. Get-only domain properties that no key
        // or index mentions must be listed, or they are silently left out (caught by the round-trip test).
        builder.Property(o => o.DistanceMeters);
        builder.Property(o => o.Status).HasConversion<string>();
        builder.Property(o => o.Version).HasColumnName("version_number");

        // Value objects are stored as columns of the order row (complex types), not separate tables.
        builder.ComplexProperty(o => o.PickupLocation, location =>
        {
            location.ComplexProperty(l => l.Point, point =>
            {
                point.Property(p => p.Latitude).HasColumnName("pickup_lat");
                point.Property(p => p.Longitude).HasColumnName("pickup_lon");
            });
            location.Property(l => l.Address).HasColumnName("pickup_address");
        });
        builder.ComplexProperty(o => o.DropoffLocation, location =>
        {
            location.ComplexProperty(l => l.Point, point =>
            {
                point.Property(p => p.Latitude).HasColumnName("dropoff_lat");
                point.Property(p => p.Longitude).HasColumnName("dropoff_lon");
            });
            location.Property(l => l.Address).HasColumnName("dropoff_address");
        });
        builder.ComplexProperty(o => o.Customer, customer =>
        {
            customer.Property(c => c.Name).HasColumnName("customer_name");
            customer.Property(c => c.Phone).HasColumnName("customer_phone");
        });
        builder.ComplexProperty(o => o.DeliveryCode, code =>
        {
            code.Property(c => c.Hash).HasColumnName("delivery_code_hash");
            code.Property(c => c.FailedAttempts).HasColumnName("delivery_code_attempts");
            code.Property(c => c.IsLocked).HasColumnName("delivery_code_locked");
        });

        // Optimistic concurrency: PostgreSQL's system column xmin changes on every row update,
        // so "UPDATE ... WHERE xmin = @loaded" updates 0 rows if someone else wrote in between.
        builder.Property<uint>("xmin").HasColumnName("xmin").IsRowVersion();

        builder.HasIndex(o => new { o.MerchantId, o.MerchantOrderRef })
            .IsUnique()
            .HasDatabaseName(MerchantOrderRefIndex);

        // Cursor pagination walks (created_at, id) newest first within a merchant.
        builder.HasIndex(o => new { o.MerchantId, o.CreatedAt, o.Id })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_orders_merchant_created");

        builder.HasIndex(o => o.Status)
            .HasFilter("status IN ('Dispatching','Unassigned','Assigned','PickedUp')")
            .HasDatabaseName("ix_orders_status");

        builder.HasMany(o => o.History)
            .WithOne()
            .HasForeignKey("OrderId")
            .IsRequired()
            .OnDelete(DeleteBehavior.Restrict);
        builder.Navigation(o => o.History).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}
