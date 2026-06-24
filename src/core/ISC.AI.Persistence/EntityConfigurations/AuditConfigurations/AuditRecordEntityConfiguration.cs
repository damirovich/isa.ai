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

        // Индекс для решётки доступа к самому журналу (ТБ-032) и быстрой выборки по времени.
        builder.HasIndex(e => new { e.Classification, e.DivisionId });
        builder.HasIndex(e => e.OccurredAt);
    }
}
