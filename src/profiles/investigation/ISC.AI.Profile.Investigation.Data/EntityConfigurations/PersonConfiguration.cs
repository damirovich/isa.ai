using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы фигурантов (<c>investigation.person</c>, ТФ-ПЕР-01).</summary>
public class PersonConfiguration : IEntityTypeConfiguration<Person>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Person> builder)
    {
        // Анкета (ТФ-ПЕР-05): год рождения при известной дате — её год. Держит PersonStore; ограничение —
        // страховка от записи в обход хранилища (иначе поиск по году и по дате давал бы разные ответы).
        builder.ToTable("person", InvestigationDbContext.Schema, table => table.HasCheckConstraint(
            "ck_person_birth_year_matches_date",
            "birth_date IS NULL OR birth_year = EXTRACT(YEAR FROM birth_date)"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.DisplayName).HasMaxLength(500).IsRequired();
        builder.Property(e => e.RoleInCase).HasMaxLength(200);
        builder.Property(e => e.Notes).HasMaxLength(4000);

        // Роль по перечню (ТФ-ПЕР-01). Умолчания в модели нет: значение всегда задаёт хранилище; у строк,
        // заведённых до перечня, миграция проставляет «иная» (3).
        builder.Property(e => e.Role).IsRequired();
        builder.Property(e => e.BirthPlace).HasMaxLength(500);
        builder.Property(e => e.WorkPlace).HasMaxLength(500);
        builder.Property(e => e.Residence).HasMaxLength(1000);
        builder.Property(e => e.Alias).HasMaxLength(200);

        // Режимные поля денормализованы с дела (ТБ-070) и NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // FK внутри схемы (ТО-инф-08): удаление дела уносит фигурантов.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        // Обычный индекс по делу: списки/счётчики фигурантов дела и каскад удаления. Частичный уникальный
        // индекс ниже для `WHERE case_id = $1` непригоден (фильтр по unidentified_number).
        builder.HasIndex(e => e.CaseId);

        // «Неустановленное лицо № N» — номер уникален в деле; установленные (NULL) индексом не ограничены.
        builder.HasIndex(e => new { e.CaseId, e.UnidentifiedNumber })
               .IsUnique()
               .HasFilter("unidentified_number IS NOT NULL");
    }
}
