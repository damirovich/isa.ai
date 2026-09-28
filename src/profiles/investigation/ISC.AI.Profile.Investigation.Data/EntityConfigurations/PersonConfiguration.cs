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
        // Связь (ТФ-ПЕР-06): поля «чья связь» и «кем приходится» — только у роли «связь» (2), и связь не
        // указывает сама на себя. Держит PersonStore; ограничение — страховка от записи в обход хранилища.
        builder.ToTable("person", InvestigationDbContext.Schema, table =>
        {
            table.HasCheckConstraint(
                "ck_person_birth_year_matches_date",
                "birth_date IS NULL OR birth_year = EXTRACT(YEAR FROM birth_date)");
            table.HasCheckConstraint(
                "ck_person_link_only_for_link_role",
                "(role = 2 OR (linked_to_person_id IS NULL AND link_type_id IS NULL))"
                + " AND (linked_to_person_id IS NULL OR linked_to_person_id <> id)");
        });
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

        // Нормализованные реквизиты для пересечений (ТО-мат-11, ТФ-ПЕР-07): индексы — под поиск совпадений
        // по всем делам ОН+УН на следующем шаге; решётка применяется к строкам поверх индекса.
        builder.Property(e => e.NameNormalized).HasMaxLength(500);
        builder.Property(e => e.ResidenceNormalized).HasMaxLength(1000);
        builder.HasIndex(e => e.NameNormalized);
        builder.HasIndex(e => e.ResidenceNormalized);

        // Связь объекта (ТФ-ПЕР-06) — FK внутри схемы. Каскад от дела удаляет фигурантов пачкой; ссылка на
        // уже удалённого «объекта» при этом обнуляется, а не блокирует удаление (SET NULL).
        builder.HasOne<Person>().WithMany()
               .HasForeignKey(e => e.LinkedToPersonId).OnDelete(DeleteBehavior.SetNull);
        builder.HasOne<ReferenceItem>().WithMany()
               .HasForeignKey(e => e.LinkTypeId).OnDelete(DeleteBehavior.Restrict);

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
