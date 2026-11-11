using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Rota.Orders.Api.Infrastructure.Configurations;

internal sealed class IdempotencyKeyConfiguration : IEntityTypeConfiguration<IdempotencyKey>
{
    public const string PrimaryKeyName = "pk_idempotency_keys";

    public void Configure(EntityTypeBuilder<IdempotencyKey> builder)
    {
        builder.ToTable("idempotency_keys");
        builder.HasKey(k => new { k.MerchantId, k.Key }).HasName(PrimaryKeyName);
        builder.Property(k => k.ResponseBody).HasColumnType("jsonb");
    }
}
