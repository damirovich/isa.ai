using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация диалогов чата по документам дела (<c>investigation.case_chat</c>, ADR-0051).</summary>
public sealed class CaseChatConfiguration : IEntityTypeConfiguration<CaseChat>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseChat> builder)
    {
        builder.ToTable("case_chat", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Title).HasMaxLength(CaseChatLimits.MaxTitleLength).IsRequired();
        builder.Property(e => e.Classification).IsRequired();

        // Переписка — материал дела: уничтожение дела (ADR-0025) уносит её каскадом внешнего ключа.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        // Список вкладки — свои диалоги одного дела.
        builder.HasIndex(e => new { e.CaseId, e.OwnerUserId });
    }
}

/// <summary>Конфигурация материалов дел в корпусе ядра (<c>investigation.case_corpus_entry</c>, ADR-0051).</summary>
public sealed class CaseCorpusEntryConfiguration : IEntityTypeConfiguration<CaseCorpusEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseCorpusEntry> builder)
    {
        builder.ToTable("case_corpus_entry", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.SourceVersion).HasMaxLength(200).IsRequired();

        // Один текст на источник; документы корпуса уничтожает уничтожение дела ДО удаления строк (каскад — после).
        builder.HasIndex(e => new { e.Kind, e.SourceId }).IsUnique();
        builder.HasIndex(e => e.CaseId);
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);
    }
}

/// <summary>Конфигурация сообщений чата по документам дела (<c>investigation.case_chat_message</c>, ADR-0051).</summary>
public sealed class CaseChatMessageConfiguration : IEntityTypeConfiguration<CaseChatMessage>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseChatMessage> builder)
    {
        builder.ToTable("case_chat_message", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Text).IsRequired();
        builder.Property(e => e.Classification).IsRequired();
        builder.Property(e => e.SourcesJson).HasColumnType("jsonb");
        builder.Property(e => e.CheckJson).HasColumnType("jsonb");

        builder.HasOne(e => e.Chat).WithMany(c => c.Messages)
               .HasForeignKey(e => e.ChatId).OnDelete(DeleteBehavior.Cascade);
    }
}
