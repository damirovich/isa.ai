using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы дел (<c>investigation.case_file</c>, ТФ-ДЕЛ-01).</summary>
public class CaseFileConfiguration : IEntityTypeConfiguration<CaseFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseFile> builder)
    {
        // ТФ-ДЕЛ-05: реквизиты задания есть РОВНО у вида «задание по объекту» (kind = 4), и у него
        // обязательные заполнены — не NULL и не одни пробелы (как IsNullOrWhiteSpace хранилища; coalesce —
        // потому что btrim(NULL) даёт NULL, а CHECK с результатом NULL пропускает строку). Ограничение —
        // последний рубеж за хранилищем: запись в обход CaseStore (скрипт сопровождения, будущий импорт
        // пакетов между офисами) не создаст задание без инициатора и не оставит скрытые реквизиты задания у
        // уголовного дела. Вид записи справочника (ГУ, а не звание) БД не проверяет — это делает хранилище.
        builder.ToTable("case_file", InvestigationDbContext.Schema, table => table.HasCheckConstraint(
            "ck_case_file_task_requisites",
            "(kind = 4 AND initiator_unit_id IS NOT NULL AND coalesce(btrim(task_number), '') <> ''"
            + " AND coalesce(btrim(justification), '') <> '' AND coalesce(btrim(purpose), '') <> '')"
            + " OR (kind <> 4 AND task_number IS NULL AND initiator_unit_id IS NULL AND initiator_name IS NULL"
            + " AND initiator_rank_id IS NULL AND initiator_position_id IS NULL AND initiator_phone IS NULL"
            + " AND initiator_details IS NULL AND justification IS NULL AND purpose IS NULL AND task_notes IS NULL)"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Number).HasMaxLength(100).IsRequired();
        builder.Property(e => e.Title).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Basis).HasMaxLength(2000);
        builder.Property(e => e.OpenedAt).HasColumnType("date");

        // Режимные поля — без умолчаний и NOT NULL (ТБ-024): дело без грифа/подразделения не создаётся.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Реквизиты задания (ТФ-ДЕЛ-05); пределы длин — те же, что у валидатора формы.
        builder.Property(e => e.TaskNumber).HasMaxLength(100);
        builder.Property(e => e.InitiatorName).HasMaxLength(300);
        builder.Property(e => e.InitiatorPhone).HasMaxLength(50);
        builder.Property(e => e.InitiatorDetails).HasMaxLength(1000);
        builder.Property(e => e.Justification).HasMaxLength(4000);
        builder.Property(e => e.Purpose).HasMaxLength(2000);
        builder.Property(e => e.TaskNotes).HasMaxLength(4000);

        // Ссылки на справочник профиля (ТФ-АДМ-07) — FK внутри схемы, удаление записи справочника
        // запрещено, пока на неё ссылается дело (записи не удаляются, а выключаются). Индексы по FK
        // EF создаёт сам; по инициатору строится архив «год → ГУ → объект» (ТФ-ДЕЛ-06).
        builder.HasOne<ReferenceItem>().WithMany()
               .HasForeignKey(e => e.InitiatorUnitId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReferenceItem>().WithMany()
               .HasForeignKey(e => e.InitiatorRankId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<ReferenceItem>().WithMany()
               .HasForeignKey(e => e.InitiatorPositionId).OnDelete(DeleteBehavior.Restrict);

        // Номер дела уникален в подразделении (CaseWriteResult.DuplicateNumber опирается на этот индекс).
        builder.HasIndex(e => new { e.DivisionId, e.Number }).IsUnique();

        // Решётка доступа (ТБ-020) — по грифу и подразделению; списки следователя — по InvestigatorUserId.
        builder.HasIndex(e => new { e.Classification, e.DivisionId });
        builder.HasIndex(e => e.InvestigatorUserId);
        builder.HasIndex(e => e.Status);

        // InvestigatorUserId / CreatedByUserId — слабые ссылки на core.app_user (ТО-инф-08): FK через
        // границу схем не создаётся, подразделение — тоже по значению (справочник в той же схеме, но
        // допуски ядра ссылаются на те же номера, поэтому FK намеренно нет — как у «Инспектора»).
    }
}
