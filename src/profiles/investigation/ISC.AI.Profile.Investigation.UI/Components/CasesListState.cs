using System;
using System.Collections.Generic;
using System.Globalization;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Состояние реестра дел (ТФ-ДЕЛ-01): отборы, страница, размер страницы и порядок. Хранится в адресе страницы — после
/// открытия дела «Назад» возвращает к тому же списку. Это только отбор внутри допуска: подставленное в адрес значение
/// ничего не открывает (ТБ-020), неразобранное — просто не отбирает.
/// </summary>
/// <param name="Text">Номер, № задания или название (подстрока).</param>
/// <param name="Kind">Вид дела.</param>
/// <param name="Status">Статус; <see langword="null"/> — все.</param>
/// <param name="DivisionId">Подразделение.</param>
/// <param name="Direction">Отдел ОН/ОУ/ОТМ (ADR-0039).</param>
/// <param name="Mine">Только дела, которые ведёт сам сотрудник.</param>
/// <param name="Page">Страница, с 1.</param>
/// <param name="PageSize">Дел на странице: 10, 25 или 50.</param>
/// <param name="Sort">Поле порядка; по умолчанию — статус («в производстве» первыми).</param>
/// <param name="SortDescending">Обратный порядок.</param>
public sealed record CasesListState(
    string? Text = null,
    CaseKind? Kind = null,
    CaseStatus? Status = null,
    int? DivisionId = null,
    CaseDirection? Direction = null,
    bool Mine = false,
    int Page = 1,
    int PageSize = CasesListState.DefaultPageSize,
    CaseSortField Sort = CaseSortField.Status,
    bool SortDescending = false)
{
    /// <summary>Дел на странице по умолчанию.</summary>
    public const int DefaultPageSize = 10;

    /// <summary>Допустимые размеры страницы.</summary>
    public static IReadOnlyList<int> PageSizes { get; } = [10, 25, 50];

    private const string TextName = "q";
    private const string KindName = "kind";
    private const string StatusName = "status";
    private const string DivisionName = "division";
    private const string MineName = "mine";
    private const string PageName = "page";
    private const string SizeName = "size";
    private const string SortName = "sort";
    private const string DescendingName = "desc";

    /// <summary>Состояние по параметрам адреса; неразобранное значение — умолчание (без отбора, 1-я страница).</summary>
    public static CasesListState Parse(IReadOnlyDictionary<string, string?> query)
    {
        ArgumentNullException.ThrowIfNull(query);

        string? Get(string name) => query.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value.Trim() : null;
        int? Int(string name) => int.TryParse(Get(name), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
        TEnum? Enum<TEnum>(string name) where TEnum : struct, Enum =>
            System.Enum.TryParse<TEnum>(Get(name), ignoreCase: true, out var value) && System.Enum.IsDefined(value) ? value : null;

        var size = Int(SizeName) is { } requested && PageSizes.Contains(requested) ? requested : DefaultPageSize;
        return new CasesListState(
            Get(TextName),
            Enum<CaseKind>(KindName),
            Enum<CaseStatus>(StatusName),
            Int(DivisionName) is > 0 and var division ? division : null,
            DirectionQuery.Parse(Get(DirectionQuery.Name)),
            Get(MineName) == "1",
            Int(PageName) is > 0 and var page ? page : 1,
            size,
            Enum<CaseSortField>(SortName) ?? CaseSortField.Status,
            Get(DescendingName) == "1");
    }

    /// <summary>Адрес реестра с этим состоянием; умолчания в адрес не пишутся.</summary>
    public string ToUri()
    {
        var parts = new List<string>();
        void Add(string name, string value) => parts.Add($"{name}={Uri.EscapeDataString(value)}");

        if (!string.IsNullOrWhiteSpace(Text)) { Add(TextName, Text.Trim()); }
        if (Kind is { } kind) { Add(KindName, kind.ToString()); }
        if (Status is { } status) { Add(StatusName, status.ToString()); }
        if (DivisionId is { } division) { Add(DivisionName, division.ToString(CultureInfo.InvariantCulture)); }
        if (Direction is { } direction) { Add(DirectionQuery.Name, DirectionQuery.Code(direction)); }
        if (Mine) { Add(MineName, "1"); }
        if (Page > 1) { Add(PageName, Page.ToString(CultureInfo.InvariantCulture)); }
        if (PageSize != DefaultPageSize) { Add(SizeName, PageSize.ToString(CultureInfo.InvariantCulture)); }
        if (Sort != CaseSortField.Status) { Add(SortName, Sort.ToString()); }
        if (SortDescending) { Add(DescendingName, "1"); }

        return parts.Count == 0 ? "/cases" : "/cases?" + string.Join('&', parts);
    }
}

/// <summary>Подписи строк реестра дел: без повторов номера у заданий.</summary>
public static class CaseListLabels
{
    /// <summary>Показывать ли «задание № …» под номером: только если номер задания отличается от номера дела.</summary>
    public static bool ShowsTaskNumber(CaseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        return row.TaskNumber is { } task && !string.Equals(task.Trim(), row.Number.Trim(), StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Название без повтора номера задания: «Задание № 14/24 — Учебный Марат» → «Учебный Марат» (так название задания
    /// пишет перенос из Access, ADR-0043); полное название — во всплывающей подсказке.
    /// </summary>
    public static string DisplayTitle(CaseRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        foreach (var number in new[] { row.TaskNumber, row.Number })
        {
            if (string.IsNullOrWhiteSpace(number))
            {
                continue;
            }

            var prefix = $"Задание № {number.Trim()}";
            if (row.Title.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = row.Title[prefix.Length..].TrimStart(' ', '—', '–', '-', ':', '·').Trim();
                if (rest.Length > 0)
                {
                    return rest;
                }
            }
        }

        return row.Title;
    }
}
