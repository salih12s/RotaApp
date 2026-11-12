using Microsoft.EntityFrameworkCore;
using Rota.BuildingBlocks.Messaging;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure;

/// <summary>The Dispatch database (<c>rota_dispatch</c>). No other service reads or writes it.</summary>
public sealed class DispatchDbContext(DbContextOptions<DispatchDbContext> options) : DbContext(options)
{
    public const string ConnectionName = "Dispatch";

    private readonly List<Func<CancellationToken, Task>> _afterCommit = [];

    public DbSet<DispatchJob> DispatchJobs => Set<DispatchJob>();

    public DbSet<Courier> Couriers => Set<Courier>();

    public DbSet<Offer> Offers => Set<Offer>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    /// <summary>
    /// Runs <paramref name="action"/> only after the current transaction commits (see <see cref="AfterCommitInterceptor"/>);
    /// a rollback drops it. For side effects that must not announce a change that never became real,
    /// such as the Redis "you have an offer" push.
    /// </summary>
    public void AfterCommit(Func<CancellationToken, Task> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _afterCommit.Add(action);
    }

    internal async Task RunAfterCommitAsync(CancellationToken cancellationToken)
    {
        var actions = _afterCommit.ToArray();
        _afterCommit.Clear();
        foreach (var action in actions)
        {
            await action(cancellationToken);
        }
    }

    internal void DiscardAfterCommit() => _afterCommit.Clear();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(DispatchDbContext).Assembly);
        modelBuilder.AddOutboxAndInbox();
    }
}
