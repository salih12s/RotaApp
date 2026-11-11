using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Orders.Api.Domain;

namespace Rota.Orders.Api.Infrastructure;

/// <summary>The Orders database (<c>rota_orders</c>). No other service reads or writes it.</summary>
public sealed class OrdersDbContext(DbContextOptions<OrdersDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Orders";

    public DbSet<Order> Orders => Set<Order>();

    public DbSet<IdempotencyKey> IdempotencyKeys => Set<IdempotencyKey>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(OrdersDbContext).Assembly);
        modelBuilder.AddOutboxAndInbox();
    }
}
