using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ISC.AI.Profile.Investigation.Data.EntityConfigurations;

/// <summary>Конфигурация истории единого поиска (<c>investigation.search_history_entry</c>, ADR-0049).</summary>
public sealed class SearchHistoryEntryConfiguration : IEntityTypeConfiguration<SearchHistoryEntry>
{
    /// <inheritdoc />
    public void Configure(EntityTypeBuilder<SearchHistoryEntry> builder)
    {
        builder.ToTable("search_history_entry", InvestigationDbContext.Schema);
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Text).HasMaxLength(SearchTerms.MaxLength).IsRequired();
        builder.Property(e => e.TextKey).HasMaxLength(SearchTerms.MaxLength).IsRequired();
        builder.Property(e => e.Classification).IsRequired();

        // История читается только своя и по времени: ключ выборки — сотрудник и время последнего поиска.
        builder.HasIndex(e => new { e.UserId, e.SearchedAt });
    }
}
