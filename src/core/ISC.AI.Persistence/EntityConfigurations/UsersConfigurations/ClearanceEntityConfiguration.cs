namespace ISC.AI.Persistence.EntityConfigurations.UsersConfigurations;

/// <summary>Конфигурация таблицы допусков (<c>core.clearance</c>).</summary>
public class ClearanceEntityConfiguration : IEntityTypeConfiguration<ClearanceEntity>
{
    public void Configure(EntityTypeBuilder<ClearanceEntity> builder)
    {
        builder.ToTable("clearance", CoreDbContext.Schema);

        builder.HasKey(e => e.Id);

        builder.Property(e => e.MaxClassification).IsRequired();

        // Разрешённые подразделения — int[] на строке допуска. Пустой массив = default-deny (ТБ-021):
        // floor-фильтр AllowedDivisions.Contains(DivisionId) не пропустит ни одного фрагмента.
        builder.Property(e => e.DivisionScope).IsRequired();

        // Частичный индекс: уникальность user_id — только среди ЖИВЫХ допусков (is_deleted = false).
        // Без фильтра отозванный (мягко удалённый) допуск навечно занимает слот пользователя — выдать
        // НОВЫЙ допуск после отзыва (штатный цикл ТБ-016: отозвать → выдать заново) невозможно, падает
        // на уникальном индексе. Найдено собственным интеграционным тестом (ClearanceAccessReaderTests).
        builder.HasIndex(e => e.UserId).IsUnique().HasFilter("is_deleted = false");
    }
}
