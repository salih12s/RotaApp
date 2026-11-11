using Microsoft.EntityFrameworkCore;

namespace Rota.BuildingBlocks.Messaging;

public static class MessagingModelBuilderExtensions
{
    /// <summary>Adds <c>outbox_messages</c> and <c>inbox_messages</c> (docs/04-data-model.md) to a service's model.</summary>
    public static ModelBuilder AddOutboxAndInbox(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        modelBuilder.Entity<OutboxMessage>(outbox =>
        {
            outbox.ToTable("outbox_messages");
            outbox.HasKey(m => m.Id);
            outbox.Property(m => m.Id).ValueGeneratedNever();
            outbox.Property(m => m.Payload).HasColumnType("jsonb");
            outbox.Property(m => m.AttemptCount).HasDefaultValue(0);

            // The relay scans only unprocessed rows, oldest first; the partial index stays small.
            outbox.HasIndex(m => m.OccurredAt)
                .HasDatabaseName("ix_outbox_unprocessed")
                .HasFilter("processed_at IS NULL");
        });

        modelBuilder.Entity<InboxMessage>(inbox =>
        {
            inbox.ToTable("inbox_messages");
            inbox.HasKey(m => new { m.MessageId, m.Consumer });
        });

        return modelBuilder;
    }
}
