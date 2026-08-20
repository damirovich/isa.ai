using System.Text.Json;
using Microsoft.EntityFrameworkCore.ChangeTracking;

namespace ISC.AI.Persistence.EntityConfigurations.CorpusConfigurations;

/// <summary>Конфигурация таблицы документов (<c>core.document</c>).</summary>
public class DocumentEntityConfiguration : IEntityTypeConfiguration<DocumentEntity>
{
    public void Configure(EntityTypeBuilder<DocumentEntity> builder)
    {
        builder.ToTable("document", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.DocType).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(1000).IsRequired();
        builder.Property(e => e.Source).HasMaxLength(700);

        // Доменный «багаж» профиля (ТО-инф-03) — НЕПРОЗРАЧНЫЙ jsonb: ядро хранит, профиль интерпретирует.
        // EF сам обрабатывает null (SQL NULL), поэтому конвертер вызывается только для непустого словаря.
        builder.Property(e => e.Metadata)
               .HasColumnType("jsonb")
               .HasConversion(
                   value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null),
                   value => JsonSerializer.Deserialize<Dictionary<string, string>>(value, (JsonSerializerOptions?)null)!,
                   new ValueComparer<Dictionary<string, string>>(
                       (left, right) => JsonSerializer.Serialize(left, (JsonSerializerOptions?)null)
                                        == JsonSerializer.Serialize(right, (JsonSerializerOptions?)null),
                       value => JsonSerializer.Serialize(value, (JsonSerializerOptions?)null).GetHashCode(StringComparison.Ordinal),
                       value => value));

        // Режимные метаданные NOT NULL — опора fail-closed фильтра доступа (ТБ-024/021).
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Индекс под фильтр доступа на извлечении (ТБ-020).
        builder.HasIndex(e => new { e.Classification, e.DivisionId });

        // Под каталог НПА на корпусе 200К+ (2026-08-19): фильтр по виду акта и сортировка по дате.
        // Поиск по заголовку (ILIKE '%…%') ускоряет trigram-индекс — он в миграции CorpusCatalogIndexes
        // прямым SQL (pg_trgm), EF-модель его не описывает.
        builder.HasIndex(e => e.DocType);
        builder.HasIndex(e => e.DocDate);

        // Дедупликация (ТНД-002) — УНИКАЛЬНЫЙ частичный индекс: БД сама отклоняет повторную загрузку того
        // же содержимого, поэтому идемпотентность держится и под КОНКУРЕНТНЫМ импортом (не только при
        // последовательном check-then-insert). Частичный (IS NOT NULL): документы без хеша не конфликтуют.
        builder.HasIndex(e => e.ContentHash)
               .IsUnique()
               .HasFilter("content_hash IS NOT NULL");
    }
}
