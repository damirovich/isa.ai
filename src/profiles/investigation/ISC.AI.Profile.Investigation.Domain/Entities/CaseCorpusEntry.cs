using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Материал дела, текст которого положен в корпус ядра для чата и поиска по смыслу (<c>investigation.case_corpus_entry</c>,
/// ADR-0051): «Сведения дела» (реквизиты, фигуранты, адреса, транспорт) или сводка/справка по бланку.
/// </summary>
/// <remarks>
/// <see cref="CoreDocumentId"/> — слабая ссылка в схему <c>core</c> без FK (ТО-инф-06). <see cref="SourceVersion"/> —
/// отметка состояния источника (для справки — номер редакции, для сведений дела — время последних изменений и число
/// записей): текст строится и кладётся в корпус заново только когда отметка изменилась. Уничтожение дела (ADR-0025)
/// сначала уничтожает документы корпуса по этим строкам, затем строки уходят каскадом.
/// </remarks>
public class CaseCorpusEntry : BaseEntity
{
    /// <summary>Дело (FK внутри схемы, каскад).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Что за материал.</summary>
    public CaseCorpusKind Kind { get; set; }

    /// <summary>Источник: для сведений дела — само дело, для сводки или справки — документ по бланку.</summary>
    public int SourceId { get; set; }

    /// <summary>Документ корпуса ядра — слабая ссылка без FK.</summary>
    public int CoreDocumentId { get; set; }

    /// <summary>Отметка состояния источника, по которой видно, что текст устарел.</summary>
    public required string SourceVersion { get; set; }

    /// <summary>Когда текст положен в корпус (UTC).</summary>
    public DateTime SyncedAt { get; set; }
}
