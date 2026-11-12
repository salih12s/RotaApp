using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Rota.Dispatch.Api.Domain;

namespace Rota.Dispatch.Api.Infrastructure.Configurations;

internal sealed class CourierConfiguration : IEntityTypeConfiguration<Courier>
{
    public void Configure(EntityTypeBuilder<Courier> builder)
    {
        builder.ToTable("couriers");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.Id).ValueGeneratedNever();

        // Get-only properties are listed on purpose: EF skips them by convention (F02 lesson).
        builder.Property(c => c.DisplayName).IsRequired();

        builder.Property(c => c.State).HasConversion<string>();
        builder.Property(c => c.Version).HasDefaultValue(0);

        builder.HasIndex(c => c.State).HasDatabaseName("ix_couriers_state");
    }
}
