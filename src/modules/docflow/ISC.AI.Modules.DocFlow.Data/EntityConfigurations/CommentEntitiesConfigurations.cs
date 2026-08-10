using ISC.AI.Modules.DocFlow.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Modules.DocFlow.Data.EntityConfigurations;

/// <summary>Конфигурация комментариев к документу (ТЗ СКИД §4.8).</summary>
public class DocumentCommentConfiguration : IEntityTypeConfiguration<DocumentComment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentComment> builder)
    {
        builder.ToTable("document_comment", DocFlowDbContext.Schema);

        // Текст — без ограничения длины в БД (Postgres text): предел 10 000 символов задаёт валидатор
        // формы, как в СКИД; хранилище не должно резать уже сохранённое при изменении лимита.
        builder.Property(c => c.Content).IsRequired();

        builder.HasOne<Document>().WithMany()
            .HasForeignKey(c => c.DocumentId).OnDelete(DeleteBehavior.Cascade);

        // Ответы: удаление корня НЕ каскадит на ветку (Restrict) — мягкое удаление сохраняет связность
        // обсуждения, дочерние ответы остаются видимыми (перенос поведения СКИД).
        builder.HasOne<DocumentComment>().WithMany()
            .HasForeignKey(c => c.ParentCommentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(c => new { c.DocumentId, c.CreatedAt });
        builder.HasIndex(c => c.ParentCommentId);
    }
}

/// <summary>Конфигурация упоминаний в комментарии.</summary>
public class DocumentCommentMentionConfiguration : IEntityTypeConfiguration<DocumentCommentMention>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentCommentMention> builder)
    {
        builder.ToTable("document_comment_mention", DocFlowDbContext.Schema);

        builder.HasOne<DocumentComment>().WithMany(c => c.Mentions)
            .HasForeignKey(m => m.CommentId).OnDelete(DeleteBehavior.Cascade);

        // Один участник упоминается в комментарии не более раза — на уровне БД (перенос из СКИД).
        builder.HasIndex(m => new { m.CommentId, m.UserId }).IsUnique();
    }
}

/// <summary>Конфигурация файлов комментария.</summary>
public class DocumentCommentFileConfiguration : IEntityTypeConfiguration<DocumentCommentFile>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<DocumentCommentFile> builder)
    {
        builder.ToTable("document_comment_file", DocFlowDbContext.Schema);
        DocumentFileConfiguration.ConfigureFileColumns(
            builder, f => f.FileName, f => f.StoredFileName, f => f.ContentType);

        builder.HasOne(f => f.Comment).WithMany(c => c.Files)
            .HasForeignKey(f => f.CommentId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(f => f.CommentId);
    }
}
