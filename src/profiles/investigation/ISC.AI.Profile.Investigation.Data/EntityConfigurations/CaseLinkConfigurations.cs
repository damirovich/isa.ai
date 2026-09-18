using ISC.AI.Profile.Investigation.Domain.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация привязок носителей (<c>investigation.case_media_link</c>, ТФ-ДЕЛ-02).</summary>
public class CaseMediaLinkConfiguration : IEntityTypeConfiguration<CaseMediaLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseMediaLink> builder)
    {
        builder.ToTable("case_media_link", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Place).HasMaxLength(1000);

        // FK внутри схемы (ТО-инф-08); MediaAssetId — по значению → media.asset, без FK через границу.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        // Носитель привязан к делу один раз (LinkMediaAsync идемпотентен); обратный поиск дела по носителю.
        builder.HasIndex(e => new { e.CaseId, e.MediaAssetId }).IsUnique();
        builder.HasIndex(e => e.MediaAssetId);
    }
}

/// <summary>Конфигурация привязок документов (<c>investigation.case_document_link</c>).</summary>
public class CaseDocumentLinkConfiguration : IEntityTypeConfiguration<CaseDocumentLink>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<CaseDocumentLink> builder)
    {
        builder.ToTable("case_document_link", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        // FK внутри схемы (ТО-инф-08); DocFlowDocumentId — по значению → docflow.document.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => new { e.CaseId, e.DocFlowDocumentId }).IsUnique();
    }
}

/// <summary>Конфигурация оснований поиска (<c>investigation.search_authorization</c>, ТБ-071).</summary>
public class SearchAuthorizationConfiguration : IEntityTypeConfiguration<SearchAuthorization>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SearchAuthorization> builder)
    {
        builder.ToTable("search_authorization", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Reference).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Notes).HasMaxLength(2000);
        builder.Property(e => e.IssuedAt).HasColumnType("date");
        builder.Property(e => e.ValidUntil).HasColumnType("date");

        // FK внутри схемы (ТО-инф-08); IssuedByUserId — по значению → core.app_user.
        builder.HasOne(e => e.Case).WithMany()
               .HasForeignKey(e => e.CaseId).OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(e => e.CaseId);
    }
}

/// <summary>Конфигурация справочника подразделений (<c>investigation.division</c>, ТФ-АДМ-01).</summary>
public class DivisionConfiguration : IEntityTypeConfiguration<Division>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<Division> builder)
    {
        builder.ToTable("division", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Name).HasMaxLength(500).IsRequired();
        builder.Property(e => e.Code).HasMaxLength(100);

        // Самоссылка иерархии (внутри схемы): родителя с детьми не удалить (Restrict). Навигаций у
        // сущности нет намеренно — справочник плоский для потребителей, иерархия только для подписи.
        builder.HasOne<Division>().WithMany()
               .HasForeignKey(e => e.ParentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(e => e.ParentId);
        builder.HasIndex(e => e.Code).IsUnique().HasFilter("code IS NOT NULL");
    }
}

/// <summary>Конфигурация ролей (<c>investigation.user_role_assignment</c>, ТП-004).</summary>
public class UserRoleAssignmentConfiguration : IEntityTypeConfiguration<UserRoleAssignment>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<UserRoleAssignment> builder)
    {
        builder.ToTable("user_role_assignment", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        // Один пользователь — одна роль; UserId — слабая ссылка на core.app_user (ТО-инф-08).
        builder.HasIndex(e => e.UserId).IsUnique();
    }
}
