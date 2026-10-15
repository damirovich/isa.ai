using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>
/// Запрос из истории единого поиска сотрудника (<c>investigation.search_history_entry</c>, ADR-0049): что искал, в каких
/// видах и отделе, сколько нашлось и когда. Личная запись: читает и меняет её только сам сотрудник.
/// </summary>
/// <remarks>
/// РЕЖИМ (ТБ-020/032). Искомый текст — чувствительные сведения: поэтому история на сервере, а не в браузере. Гриф строки —
/// допуск сотрудника в момент поиска (ничего выше он найти и не мог); показывается строка только при текущем допуске не
/// ниже этого грифа. Подразделения у строки нет: она не материал дела, а запись о действии самого сотрудника.
/// Удаление строки из истории ничего не стирает из журнала аудита — там поиск записан отдельно (ADR-0048).
/// </remarks>
public class SearchHistoryEntry : BaseEntity
{
    /// <summary>Сотрудник — владелец записи (идентификатор учётной записи ядра; FK через границу схем нет).</summary>
    public int UserId { get; set; }

    /// <summary>Запрос как ввели (обрезанный).</summary>
    public required string Text { get; set; }

    /// <summary>Ключ повтора: запрос без учёта регистра и «ё/е» (<see cref="SearchTerms.Text"/>).</summary>
    public required string TextKey { get; set; }

    /// <summary>Виды выдачи.</summary>
    public SearchKinds Kinds { get; set; }

    /// <summary>Отдел или <see langword="null"/> — все доступные дела.</summary>
    public CaseDirection? Direction { get; set; }

    /// <summary>Сколько строк нашлось при последнем поиске (по разделам — не больше предела выдачи).</summary>
    public int ResultCount { get; set; }

    /// <summary>Закреплён: не вытесняется новыми запросами и показывается первым.</summary>
    public bool IsPinned { get; set; }

    /// <summary>Гриф — допуск сотрудника в момент поиска. NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Когда искали в последний раз (UTC).</summary>
    public DateTime SearchedAt { get; set; }
}
