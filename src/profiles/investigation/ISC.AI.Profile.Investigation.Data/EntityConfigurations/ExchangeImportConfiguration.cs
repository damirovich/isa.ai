using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Журнал приёмов пакетов обмена (ADR-0068 v0.2).</summary>
public sealed class ExchangeImportConfiguration : IEntityTypeConfiguration<ExchangeImport>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangeImport> builder)
    {
        builder.ToTable("exchange_import", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PackageName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.SourceInstance).HasMaxLength(32);
        builder.Property(e => e.Period).HasMaxLength(7);
        builder.Property(e => e.ManifestSha256).HasMaxLength(64);
        builder.Property(e => e.Error).HasMaxLength(2000);
        builder.Property(e => e.Reconciliation).HasMaxLength(4000);

        // Журнал — свежие сверху; «идёт ли приём» — по состоянию; итог прежних приёмов пакета — по имени.
        builder.HasIndex(e => e.StartedAt);
        builder.HasIndex(e => e.Status);
        builder.HasIndex(e => e.PackageName);
    }
}

/// <summary>Карта «запись источника → своя запись» (ADR-0068 v0.2, ЦС-08).</summary>
public sealed class ExchangeSourceRecordConfiguration : IEntityTypeConfiguration<ExchangeSourceRecord>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangeSourceRecord> builder)
    {
        builder.ToTable("exchange_source_record", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceInstance).HasMaxLength(32).IsRequired();

        // ИНВАРИАНТ идемпотентности: запись источника сопоставлена не больше одного раза — повтор приёма дубля не заведёт
        // даже при гонке двух заданий (второе упадёт на индексе, а не продублирует дело).
        builder.HasIndex(e => new { e.SourceInstance, e.Kind, e.SourceId }).IsUnique();
        builder.HasIndex(e => new { e.Kind, e.LocalId });
        builder.HasOne<ExchangeImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Конфликты приёма и решения Руководителя по ним (ADR-0068 v0.2–v0.3).</summary>
public sealed class ExchangeImportConflictConfiguration : IEntityTypeConfiguration<ExchangeImportConflict>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangeImportConflict> builder)
    {
        // Решение «создать новое» — только для дела (одинаковый файл — один носитель, ТФ-МЕД-04); решённый и применённый
        // конфликт обязан нести решение — иначе приём не знал бы, что применять.
        builder.ToTable("exchange_import_conflict", InvestigationDbContext.Schema, table =>
        {
            table.HasCheckConstraint("ck_exchange_conflict_new_case", "decision IS NULL OR decision <> 2 OR kind = 1");
            table.HasCheckConstraint("ck_exchange_conflict_decided", "status NOT IN (3, 4) OR decision IS NOT NULL");
        });
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceInstance).HasMaxLength(32).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Label).HasMaxLength(500);
        builder.Property(e => e.Details).HasMaxLength(2000);
        builder.Property(e => e.DecisionNumber).HasMaxLength(100);
        builder.Property(e => e.DecisionComment).HasMaxLength(1000);

        // Один конфликт на запись источника: повторный приём обновляет его, а не множит очередь.
        builder.HasIndex(e => new { e.SourceInstance, e.Kind, e.SourceId }).IsUnique();
        builder.HasIndex(e => e.Status);
        builder.HasOne<ExchangeImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>Эталоны и появления пакета, ждущие лица в индексации получателя (ADR-0068 v0.3).</summary>
public sealed class ExchangePendingFaceConfiguration : IEntityTypeConfiguration<ExchangePendingFace>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<ExchangePendingFace> builder)
    {
        builder.ToTable("exchange_pending_face", InvestigationDbContext.Schema, table =>
            table.HasCheckConstraint("ck_exchange_pending_face_kind", "kind IN (10, 11)"));
        builder.HasKey(e => e.Id);
        builder.Property(e => e.SourceInstance).HasMaxLength(32).IsRequired();
        builder.Property(e => e.PackageName).HasMaxLength(200).IsRequired();
        builder.Property(e => e.Payload).IsRequired();
        builder.Property(e => e.Reason).HasMaxLength(500);

        // Одна строка на запись источника: повторный приём освежает её, а не множит ожидание.
        builder.HasIndex(e => new { e.SourceInstance, e.Kind, e.SourceId }).IsUnique();
        builder.HasIndex(e => e.Status);
        builder.HasOne<ExchangeImport>().WithMany().HasForeignKey(e => e.ImportId).OnDelete(DeleteBehavior.Restrict);
    }
}
