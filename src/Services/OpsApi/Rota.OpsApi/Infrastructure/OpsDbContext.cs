using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.BuildingBlocks.Messaging;

namespace Rota.OpsApi.Infrastructure;

/// <summary>
/// The Ops API database (<c>rota_ops</c>): a read model built only from events (docs/04 §Ops API). Rows are written by the
/// consumers with conditional SQL (never "read, compare, write"), and can be thrown away and rebuilt. No customer data is copied here (KVKK).
/// </summary>
public sealed class OpsDbContext(DbContextOptions<OpsDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Ops";

    public DbSet<OrderView> Orders => Set<OrderView>();

    public DbSet<CourierView> Couriers => Set<CourierView>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OpsDbContext).Assembly);
        modelBuilder.AddOutboxAndInbox();
    }
}

/// <summary>One order as the dispatcher panel sees it. Status is text, the same words Orders uses.</summary>
public sealed class OrderView
{
    public Guid OrderId { get; init; }

    public string MerchantId { get; init; } = string.Empty;

    public string MerchantOrderRef { get; init; } = string.Empty;

    public OrderStatus Status { get; init; }

    public Guid? CourierId { get; init; }

    public double PickupLat { get; init; }

    public double PickupLon { get; init; }

    public double DropoffLat { get; init; }

    public double DropoffLon { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset? AssignedAt { get; init; }

    public DateTimeOffset? PickedUpAt { get; init; }

    public DateTimeOffset? DeliveredAt { get; init; }

    /// <summary>Only an event with a higher version than this is applied.</summary>
    public int OrderVersion { get; init; }
}

public sealed class CourierView
{
    public Guid CourierId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public CourierState State { get; init; }

    public Guid? CurrentOrder { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    /// <summary>Same idea as <see cref="OrderView.OrderVersion"/>, for <c>CourierStateChanged.v1</c>.</summary>
    public int CourierVersion { get; init; }
}

internal sealed class OrderViewConfiguration : IEntityTypeConfiguration<OrderView>
{
    public void Configure(EntityTypeBuilder<OrderView> builder)
    {
        builder.ToTable("order_view");
        builder.HasKey(o => o.OrderId);
        builder.Property(o => o.OrderId).ValueGeneratedNever();
        builder.Property(o => o.MerchantId).IsRequired();
        builder.Property(o => o.MerchantOrderRef).IsRequired();
        builder.Property(o => o.Status).HasConversion<string>().IsRequired();

        // docs/04: the panel's main list is "by status, newest first"; the id breaks ties so cursor paging is stable.
        builder.HasIndex(o => new { o.Status, o.CreatedAt, o.OrderId })
            .IsDescending(false, true, true)
            .HasDatabaseName("ix_order_view_status_created");
    }
}

internal sealed class CourierViewConfiguration : IEntityTypeConfiguration<CourierView>
{
    public void Configure(EntityTypeBuilder<CourierView> builder)
    {
        builder.ToTable("courier_view");
        builder.HasKey(c => c.CourierId);
        builder.Property(c => c.CourierId).ValueGeneratedNever();
        builder.Property(c => c.DisplayName).IsRequired();
        builder.Property(c => c.State).HasConversion<string>().IsRequired();
    }
}

/// <summary>The words Orders uses for a status (docs/02). Stored as text, exposed as a GraphQL enum.</summary>
public enum OrderStatus
{
    Created,
    Dispatching,
    Unassigned,
    Assigned,
    PickedUp,
    Delivered,
    DeliveryFailed,
    Returned,
    Cancelled,
}

/// <summary>The words Dispatch uses for a courier's state.</summary>
public enum CourierState
{
    Offline,
    Available,
    Offered,
    OnDelivery,
}

/// <summary>Creates short-lived contexts on demand (for GraphQL resolvers and DataLoaders), with the same options as the scoped context.</summary>
public sealed class OpsDbContextFactory(DbContextOptions<OpsDbContext> options) : IDbContextFactory<OpsDbContext>
{
    public OpsDbContext CreateDbContext() => new(options);
}
