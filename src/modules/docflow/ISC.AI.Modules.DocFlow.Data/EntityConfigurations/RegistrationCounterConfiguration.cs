using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>
/// Конфигурация счётчика журнала регистрации (<c>docflow.reg_counter</c>): составной ключ
/// «направление × год» — по строке на журнал. Строка обновляется атомарным UPSERT'ом
/// (см. <c>DocumentStore.NextRegNumberAsync</c>), через EF-трекинг не пишется.
/// </summary>
public class RegistrationCounterConfiguration : IEntityTypeConfiguration<RegistrationCounter>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<RegistrationCounter> builder)
    {
        builder.ToTable("reg_counter", DocFlowDbContext.Schema);
        builder.HasKey(e => new { e.Direction, e.Year });
    }
}
