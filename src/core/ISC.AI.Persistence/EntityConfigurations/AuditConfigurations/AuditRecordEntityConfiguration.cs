namespace ISC.AI.Persistence.EntityConfigurations.AuditConfigurations;

/// <summary>Конфигурация неизменяемого журнала аудита (<c>core.audit_record</c>, ТБ-030/031/032).</summary>
public class AuditRecordEntityConfiguration : IEntityTypeConfiguration<AuditRecordEntity>
{
    public void Configure(EntityTypeBuilder<AuditRecordEntity> builder)
    {
        builder.ToTable("audit_record", CoreDbContext.Schema);

        // bigint-последовательность (монотонность для упорядочения цепочки, ТБ-031).
        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).UseIdentityByDefaultColumn();

        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.ObjectRef).HasMaxLength(2000);

        // Хеш-цепочка (ТБ-031): обе колонки обязательны.
        builder.Property(e => e.PrevHash).IsRequired();
        builder.Property(e => e.RecordHash).IsRequired();

        // Контекст записи (ТБ-080, ADR-0066). Версия хеша обязательна: проверка цепочки выбирает по ней формулу.
        builder.Property(e => e.ClientAddress).HasMaxLength(AuditRecordEntity.MaxClientAddressLength);
        builder.Property(e => e.HashVersion).IsRequired();

        // «Все действия сеанса» — отбор журнала по номеру сеанса.
        builder.HasIndex(e => e.SessionId);

        // Индекс для решётки доступа к самому журналу (ТБ-032) и быстрой выборки по времени.
        builder.HasIndex(e => new { e.Classification, e.DivisionId });
        builder.HasIndex(e => e.OccurredAt);
    }
}
