using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.Orders.Api.Domain;

namespace Rota.Orders.Api.Infrastructure.Configurations;

internal sealed class OrderStatusChangeConfiguration : IEntityTypeConfiguration<OrderStatusChange>
{
    public void Configure(EntityTypeBuilder<OrderStatusChange> builder)
    {
        builder.ToTable("order_status_history");

        // The domain record has no identity; the table's bigint key lives only in the model.
        builder.Property<long>("Id").UseIdentityAlwaysColumn();
        builder.HasKey("Id");

        builder.Property(h => h.From).HasColumnName("from_status").HasConversion<string>();
        builder.Property(h => h.To).HasColumnName("to_status").HasConversion<string>();
        builder.ComplexProperty(h => h.Actor, actor =>
        {
            actor.Property(a => a.Type).HasColumnName("actor_type").HasConversion<string>();
            actor.Property(a => a.Id).HasColumnName("actor_id");
        });
    }
}
