using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>The Dispatch database (<c>rota_dispatch</c>). No other service reads or writes it.</summary>
public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Dispatch";

    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DispatchDbContext).Assembly);
        modelBuilder.AddOutboxAndInbox();
    }
}
