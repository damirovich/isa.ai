using System.Text.Json;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Строка хронологической таблицы «Сводки ОН» (ТФ-ДДЛ-04).</summary>
/// <param name="Time">Время (как записано: «09:15», «около 10 ч»).</param>
/// <param name="Place">Место или адрес.</param>
/// <param name="Description">Событие, маршрут, действия объекта.</param>
/// <param name="Persons">Лица (контакты объекта).</param>
/// <param name="Vehicles">Транспорт (марка, госномер).</param>
public sealed record SummaryEvent(string? Time, string? Place, string? Description, string? Persons, string? Vehicles);

/// <summary>
/// Поля бланка (ТФ-ДДЛ-04, ADR-0031 п. 1–2). Один тип на оба вида: «Сводка ОН» заполняет <see cref="Events"/>,
/// «Справка УН» — разделы справки; <see cref="Conclusion"/> — у обоих. Хранится в <c>jsonb</c> редакции;
/// формат расширяется только ДОБАВЛЕНИЕМ полей — старые редакции читаются с новыми полями пустыми.
/// </summary>
/// <param name="Events">Хронология событий (сводка).</param>
/// <param name="Identity">Установочные данные (справка).</param>
/// <param name="Addresses">Адреса проживания и пребывания (справка).</param>
/// <param name="Occupation">Род занятий, место работы (справка).</param>
/// <param name="Family">Семейные и иные связи (справка).</param>
/// <param name="Characterizing">Характеризующие материалы (справка).</param>
/// <param name="Conclusion">Вывод / примечание.</param>
public sealed record CaseReportContent(
    IReadOnlyList<SummaryEvent>? Events = null,
    string? Identity = null,
    string? Addresses = null,
    string? Occupation = null,
    string? Family = null,
    string? Characterizing = null,
    string? Conclusion = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Пустой бланк.</summary>
    public static CaseReportContent Empty { get; } = new();

    /// <summary>Строки таблицы событий (никогда не <see langword="null"/>).</summary>
    public IReadOnlyList<SummaryEvent> EventRows => Events ?? [];

    /// <summary>Сериализация в JSON для колонки редакции.</summary>
    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Чтение из JSON редакции; пусто или битое — пустой бланк (старая редакция не ломает карточку).</summary>
    public static CaseReportContent FromJson(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Empty;
        }

        try
        {
            return JsonSerializer.Deserialize<CaseReportContent>(json, Json) ?? Empty;
        }
        catch (JsonException)
        {
            return Empty;
        }
    }

    /// <summary>
    /// Нормализованный текст всех полей вида <paramref name="kind"/> для поиска подстрокой (ТО-мат-11). Поля
    /// другого вида не учитываются: в справку не «протекает» то, что в ней не показывается.
    /// </summary>
    public string SearchText(CaseReportKind kind)
    {
        var parts = new List<string?>();
        if (kind == CaseReportKind.SummaryOn)
        {
            foreach (var e in EventRows)
            {
                parts.AddRange([e.Time, e.Place, e.Description, e.Persons, e.Vehicles]);
            }
        }
        else
        {
            parts.AddRange([Identity, Addresses, Occupation, Family, Characterizing]);
        }

        parts.Add(Conclusion);
        return RequisiteNormalizer.SearchText(string.Join(' ', parts.Where(p => !string.IsNullOrWhiteSpace(p))));
    }

    /// <summary>
    /// Бланк, приведённый к виду: пустые строки событий отброшены, у справки нет событий, у сводки — разделов
    /// справки; пробелы по краям сняты. Хранится именно такой вид — сравнение редакций не зависит от мусора.
    /// </summary>
    public CaseReportContent Normalize(CaseReportKind kind)
    {
        static string? Clean(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

        if (kind == CaseReportKind.SummaryOn)
        {
            var events = EventRows
                .Select(e => new SummaryEvent(Clean(e.Time), Clean(e.Place), Clean(e.Description), Clean(e.Persons), Clean(e.Vehicles)))
                .Where(e => e.Time is not null || e.Place is not null || e.Description is not null || e.Persons is not null || e.Vehicles is not null)
                .ToList();
            return new CaseReportContent(Events: events, Conclusion: Clean(Conclusion));
        }

        return new CaseReportContent(
            Identity: Clean(Identity),
            Addresses: Clean(Addresses),
            Occupation: Clean(Occupation),
            Family: Clean(Family),
            Characterizing: Clean(Characterizing),
            Conclusion: Clean(Conclusion));
    }
}

/// <summary>
/// Правила документов по бланку (ТФ-ДДЛ-05, ADR-0031 п. 4–5): длина окна редактирования и срок разрешения
/// на правку архивного документа. По умолчанию — 48 и 24 часа (умолчание вопроса 4 Приложения В ТЗ).
/// </summary>
/// <param name="EditWindowHours">Окно свободной правки от создания документа, часов.</param>
/// <param name="PermitHours">Срок действия разрешения Администратора, часов.</param>
public sealed record CaseReportOptions(int EditWindowHours = 48, int PermitHours = 24)
{
    /// <summary>Ключ конфигурации окна редактирования.</summary>
    public const string EditWindowKey = "Investigation:Reports:EditWindowHours";

    /// <summary>Ключ конфигурации срока разрешения.</summary>
    public const string PermitHoursKey = "Investigation:Reports:PermitHours";

    /// <summary>Окно редактирования.</summary>
    public TimeSpan EditWindow => TimeSpan.FromHours(EditWindowHours);

    /// <summary>Срок разрешения.</summary>
    public TimeSpan PermitDuration => TimeSpan.FromHours(PermitHours);

    /// <summary>
    /// Состояние документа на момент <paramref name="nowUtc"/>: неактивный — всегда «неактивный»; иначе окно
    /// открыто строго до <c>создан + окно</c>.
    /// </summary>
    public CaseReportState StateAt(DateTime createdAtUtc, bool isActive, DateTime nowUtc) =>
        !isActive ? CaseReportState.Inactive
        : nowUtc < createdAtUtc + EditWindow ? CaseReportState.Editable
        : CaseReportState.Archived;
}
