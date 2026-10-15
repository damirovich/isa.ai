using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Мостик «расшифровка носителя ↔ документ корпуса ядра» (<c>media.transcript_index_link</c>, ADR-0051): текст готовой
/// расшифровки проиндексирован в <c>core.document</c>/<c>core.chunk</c> для поиска по смыслу и чата по материалам.
/// </summary>
/// <remarks>
/// <see cref="CoreDocumentId"/> — слабая ссылка по значению через границу схем, без FK (ТО-инф-06). Одна строка на
/// носитель; уничтожение носителя (ТБ-064) сначала уничтожает документ корпуса, затем строка уходит каскадом.
/// </remarks>
public class TranscriptIndexLink : BaseEntity
{
    /// <summary>Носитель (FK внутри схемы, каскад).</summary>
    public int AssetId { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Документ корпуса ядра — слабая ссылка без FK.</summary>
    public int CoreDocumentId { get; set; }

    /// <summary>Когда проиндексировано (UTC) — сравнивается со временем расшифровки, чтобы найти устаревшие.</summary>
    public DateTime IndexedAt { get; set; }
}
