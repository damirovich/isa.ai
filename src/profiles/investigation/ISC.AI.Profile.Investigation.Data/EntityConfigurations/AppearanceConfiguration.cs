using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация таблицы появлений (<c>investigation.appearance</c>, ТФ-ПЕР-02, ТБ-073).</summary>
public class AppearanceConfiguration : IEntityTypeConfiguration<Appearance>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Appearance> builder)
    {
        // ИНВАРИАНТ ПРОИСХОЖДЕНИЯ (ТБ-073, ADR-0068 v0.3): локальное появление — только из подтверждённого кандидата
        // (сеанс, кандидат и первая подпись заданы); без них — только перенесённое из другого офиса, с пометкой источника.
        // Ограничение держит это и против записи в обход хранилища.
        builder.ToTable("appearance", InvestigationDbContext.Schema, table => table.HasCheckConstraint(
            "ck_appearance_origin",
            "(search_session_id IS NOT NULL AND candidate_id IS NOT NULL AND expert_user_id IS NOT NULL) OR source_instance IS NOT NULL"));
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.DivisionId).IsRequired();
        builder.Property(e => e.Status).IsRequired();
        builder.Property(e => e.RevokeReason).HasMaxLength(1000);
        builder.Property(e => e.SourceInstance).HasMaxLength(32);

        // FK внутри схемы (ТО-инф-08); носитель/лицо/сессия/кандидат — по значению → схема media.
        builder.HasOne(e => e.Person).WithMany()
               .HasForeignKey(e => e.PersonId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.CaseId);
        builder.HasIndex(e => e.MediaAssetId);

        // Один кандидат — одно появление: повторное подтверждение того же кандидата не плодит записей (ТБ-073).
        builder.HasIndex(e => e.CandidateId).IsUnique();
    }
}
