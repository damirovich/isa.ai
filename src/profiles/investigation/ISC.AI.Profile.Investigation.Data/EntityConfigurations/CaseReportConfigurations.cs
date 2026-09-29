using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация сводок и справок (<c>investigation.case_report</c>, ТФ-ДДЛ-04, ADR-0031).</summary>
public class CaseReportConfiguration : IEntityTypeConfiguration<CaseReport>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseReport> builder)
    {
        builder.ToTable("case_report", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Kind).IsRequired();
        builder.Property(e => e.ReportDate).IsRequired();
        builder.Property(e => e.IsActive).IsRequired();
        builder.Property(e => e.CurrentRevision).IsRequired();
        builder.Property(e => e.SearchText).IsRequired();

        // Режимные поля — с дела (ТБ-070), NOT NULL.
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        // Уничтожение дела (ADR-0025) уносит документы каскадом; удаление фигуранта-объекта документ не
        // удаляет — ссылка обнуляется, документ дела остаётся (удаления сводок нет, ТФ-ДДЛ-05).
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);
        builder.HasOne<Person>().WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.SetNull);

        // Список дела — по делу и дате («папка даты»).
        builder.HasIndex(e => new { e.CaseId, e.ReportDate });
    }
}

/// <summary>Конфигурация редакций (<c>investigation.case_report_revision</c>, ТФ-ДДЛ-05).</summary>
public class CaseReportRevisionConfiguration : IEntityTypeConfiguration<CaseReportRevision>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseReportRevision> builder)
    {
        builder.ToTable("case_report_revision", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Number).IsRequired();
        builder.Property(e => e.ContentJson).HasColumnType("jsonb").IsRequired();
        builder.Property(e => e.EditReason).HasMaxLength(2000);

        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        builder.HasOne(e => e.Report).WithMany()
               .HasForeignKey(e => e.ReportId).OnDelete(DeleteBehavior.Cascade);

        // Последний рубеж конкурентной правки (ADR-0031 п. 6): две редакции с одним номером невозможны.
        builder.HasIndex(e => new { e.ReportId, e.Number }).IsUnique();
    }
}

/// <summary>Конфигурация запросов на правку (<c>investigation.case_report_permit</c>, ТФ-АДМ-06).</summary>
public class CaseReportPermitConfiguration : IEntityTypeConfiguration<CaseReportPermit>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseReportPermit> builder)
    {
        builder.ToTable("case_report_permit", InvestigationDbContext.Schema, table => table.HasCheckConstraint(
            "ck_case_report_permit_reason", "btrim(reason) <> ''"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Reason).HasMaxLength(2000).IsRequired();
        builder.Property(e => e.RequestedAt).IsRequired();
        builder.Property(e => e.Status).IsRequired();

        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();

        builder.HasOne(e => e.Report).WithMany()
               .HasForeignKey(e => e.ReportId).OnDelete(DeleteBehavior.Cascade);

        // Очередь Администратора — по статусу; запросы субъекта по документу.
        builder.HasIndex(e => new { e.Status, e.RequestedAt });
        builder.HasIndex(e => new { e.ReportId, e.RequestedByUserId });
    }
}
