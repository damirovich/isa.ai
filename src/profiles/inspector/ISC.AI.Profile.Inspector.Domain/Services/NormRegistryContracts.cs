using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты КАРТОТЕКИ НПА (ТФ-НПА-02): реестр норм, редакции, связь с корпусом ядра.
// Разрез «интерфейс отдельно, контракты по агрегату» — как DivisionContracts.cs.

/// <summary>Отбор реестра норм: текст ищется в номере и названии; статус — по действующей редакции.</summary>
public sealed record NormListFilter(
    string? Text = null,
    RevisionStatus? Status = null,
    int Page = 1,
    int PageSize = 25);

/// <summary>
/// Строка реестра. <paramref name="CurrentStatus"/> — статус «ведущей» редакции (действующая, если
/// есть; иначе последняя по дате вступления); <see langword="null"/> — редакций ещё нет.
/// </summary>
public sealed record NormListItem(
    int Id,
    string Identifier,
    string Title,
    int RevisionCount,
    RevisionStatus? CurrentStatus,
    DateOnly? LatestEffectiveDate,
    int LinkedDocuments);

/// <summary>Страница реестра: строки + общее число до среза (для навигации).</summary>
public sealed record NormRegistryPage(IReadOnlyList<NormListItem> Rows, int TotalCount);

/// <summary>Редакция в карточке нормы; <paramref name="ChunkCount"/> — сколько чанков корпуса привязано.</summary>
public sealed record NormRevisionItem(
    int Id,
    RevisionStatus Status,
    DateOnly EffectiveDate,
    DateOnly? RepealedDate,
    int ChunkCount);

/// <summary>
/// Привязанный документ корпуса. Ссылка — слабая, по значению (ТО-инф-06), поэтому документ мог
/// быть удалён из корпуса гарантированным удалением: <paramref name="IsAlive"/> = false — «висячая»
/// связка, карточка честно это показывает, а не прячет.
/// </summary>
public sealed record LinkedCorpusDocument(
    int DocumentId,
    string? Title,
    string? DocType,
    DateOnly? DocDate,
    short? Classification,
    bool IsAlive);

/// <summary>Карточка нормы: реквизиты + редакции (новые первыми) + привязанные документы корпуса.</summary>
public sealed record NormDetails(
    int Id,
    string Identifier,
    string Title,
    IReadOnlyList<NormRevisionItem> Revisions,
    IReadOnlyList<LinkedCorpusDocument> Documents);

/// <summary>Документ корпуса — кандидат на привязку (диалог поиска в карточке).</summary>
public sealed record CorpusDocumentCandidate(
    int DocumentId,
    string Title,
    string DocType,
    DateOnly? DocDate,
    short Classification);

/// <summary>Итог операции ведения картотеки.</summary>
public enum NormWriteResult
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Норма (или редакция/документ операции) не найдена.</summary>
    NotFound,

    /// <summary>Норма с таким номером уже есть (номер — ключ грунтовки, дубли запрещены).</summary>
    DuplicateIdentifier,

    /// <summary>Документ корпуса уже привязан к этой норме.</summary>
    AlreadyLinked,
}
