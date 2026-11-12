using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.BuildingBlocks.Messaging;
using Rota.Notifications.Api.Domain;

namespace Rota.Notifications.Api.Infrastructure;

/// <summary>The Notifications database (<c>rota_notifications</c>). No other service reads or writes it.</summary>
public sealed class NotificationsDbContext(DbContextOptions<NotificationsDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Notifications";

    public DbSet<WebhookSubscription> Subscriptions => Set<WebhookSubscription>();

    public DbSet<WebhookDelivery> Deliveries => Set<WebhookDelivery>();

    public DbSet<CourierName> CourierNames => Set<CourierName>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(NotificationsDbContext).Assembly);
        modelBuilder.AddOutboxAndInbox();
    }
}

internal sealed class WebhookSubscriptionConfiguration : IEntityTypeConfiguration<WebhookSubscription>
{
    public void Configure(EntityTypeBuilder<WebhookSubscription> builder)
    {
        builder.ToTable("webhook_subscriptions");
        builder.HasKey(s => s.MerchantId);
        builder.Property(s => s.MerchantId).ValueGeneratedNever();
        builder.Property(s => s.Url).IsRequired();
        builder.Property(s => s.SecretProtected).IsRequired();
    }
}

internal sealed class WebhookDeliveryConfiguration : IEntityTypeConfiguration<WebhookDelivery>
{
    public void Configure(EntityTypeBuilder<WebhookDelivery> builder)
    {
        builder.ToTable("webhook_deliveries");
        builder.HasKey(d => d.Id);
        builder.Property(d => d.Id).ValueGeneratedNever();
        builder.Property(d => d.MerchantId).IsRequired();
        builder.Property(d => d.EventType).IsRequired();
        builder.Property(d => d.Payload).HasColumnType("jsonb").IsRequired();
        builder.Property(d => d.Status).HasConversion<string>();
        builder.Property(d => d.AttemptCount).HasDefaultValue(0);

        // One delivery per merchant per event: the database refuses a duplicate even if the inbox did not catch it.
        builder.HasIndex(d => new { d.MerchantId, d.EventId }).IsUnique();

        // The worker looks for due rows only among those still pending; the dead-letter list only among the dead ones.
        builder.HasIndex(d => d.NextAttemptAt).HasFilter("status = 'Pending'").HasDatabaseName("ix_deliveries_due");
        builder.HasIndex(d => new { d.MerchantId, d.CreatedAt }).IsDescending(false, true).HasFilter("status = 'Dead'").HasDatabaseName("ix_deliveries_dead");
    }
}

internal sealed class CourierNameConfiguration : IEntityTypeConfiguration<CourierName>
{
    public void Configure(EntityTypeBuilder<CourierName> builder)
    {
        builder.ToTable("courier_names");
        builder.HasKey(c => c.CourierId);
        builder.Property(c => c.CourierId).ValueGeneratedNever();
        builder.Property(c => c.DisplayName).IsRequired();
    }
}
