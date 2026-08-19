namespace ISC.AI.Profile.Inspector.Domain.Services;

// Контракты КАТАЛОГА КОРПУСА НПА (ТФ-НПА-01/02): реестр документов корпуса со статусом действия —
// вид «как в ЦБД Минюста»: список, галочка/крестик, фильтры, карточка с текстом.
// Разрез «интерфейс отдельно, контракты по агрегату» — как NormRegistryContracts.cs.

/// <summary>Статус действия документа — как его знает система (по флагу годности чанков, GATE-3).</summary>
public enum CorpusDocumentCurrency
{
    /// <summary>Все фрагменты действующие.</summary>
    Current,

    /// <summary>Все фрагменты погашены (утратила силу редакция или документ заменён новой версией).</summary>
    Superseded,

    /// <summary>Часть фрагментов погашена — переходное состояние, показывается честно.</summary>
    Mixed,

    /// <summary>
    /// Текста нет (ни одного фрагмента: скан без текстового слоя, пустой файл). Ни «действует»,
    /// ни «утратил силу» — нечего ни выдавать, ни гасить; показывается честно отдельным состоянием.
    /// </summary>
    NoText,
}

/// <summary>Отбор каталога. Статус <see langword="null"/> — все; <see cref="CorpusDocumentCurrency.Current"/> — только действующие.</summary>
public sealed record CorpusCatalogFilter(
    string? Text = null,
    string? DocType = null,
    CorpusDocumentCurrency? Currency = null,
    CorpusCatalogSort Sort = CorpusCatalogSort.DateDesc,
    int Page = 1,
    int PageSize = 25);

/// <summary>Порядок каталога.</summary>
public enum CorpusCatalogSort
{
    /// <summary>По дате документа, новые первыми (без даты — в конце).</summary>
    DateDesc,

    /// <summary>По дате документа, старые первыми.</summary>
    DateAsc,

    /// <summary>По заголовку А→Я.</summary>
    TitleAsc,

    /// <summary>По времени загрузки в корпус, новые первыми.</summary>
    LoadedDesc,
}

/// <summary>Строка каталога.</summary>
public sealed record CorpusCatalogItem(
    int DocumentId,
    string Title,
    string DocType,
    DateOnly? DocDate,
    short Classification,
    CorpusDocumentCurrency Currency,
    int ChunkCount,
    DateTime LoadedAt);

/// <summary>Страница каталога: строки + общее число до среза + виды актов для фильтра.</summary>
public sealed record CorpusCatalogPage(
    IReadOnlyList<CorpusCatalogItem> Rows,
    int TotalCount,
    IReadOnlyList<string> DocTypes);

/// <summary>Фрагмент текста документа в карточке — по порядку, с признаком актуальности.</summary>
public sealed record CorpusDocumentFragment(int Ordinal, string Text, bool IsCurrent);

/// <summary>
/// Карточка документа корпуса. <paramref name="SupersededByDocumentId"/> — преемник, если документ
/// заменён новой версией; <paramref name="LinkedNorms"/> — нормы картотеки, к которым он привязан.
/// </summary>
public sealed record CorpusDocumentDetails(
    int DocumentId,
    string Title,
    string DocType,
    string? Source,
    DateOnly? DocDate,
    short Classification,
    int DivisionId,
    CorpusDocumentCurrency Currency,
    int? SupersededByDocumentId,
    DateTime LoadedAt,
    IReadOnlyList<CorpusDocumentFragment> Fragments,
    IReadOnlyList<CorpusLinkedNorm> LinkedNorms);

/// <summary>Норма картотеки, к которой привязан документ.</summary>
public sealed record CorpusLinkedNorm(int NormId, string Identifier, string Title);
