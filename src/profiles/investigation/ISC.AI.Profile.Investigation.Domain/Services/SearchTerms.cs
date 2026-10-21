using System.Globalization;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Запрос единого поиска (ТФ-ПСК-01, ADR-0048), разобранный один раз для всех видов выдачи по правилам ТО-мат-11:
/// текст — для подстрочного поиска (<see cref="RequisiteNormalizer.SearchText"/>: нижний регистр, «ё» → «е», ө/ү/ң
/// сохраняются), госномер — нормализованный (<see cref="RequisiteNormalizer.Plate"/>), адрес — нормализованный
/// (<see cref="RequisiteNormalizer.Address"/>), номер телефона — нормализованный (<see cref="RequisiteNormalizer.Phone"/>,
/// ADR-0055), дата и год — для даты рождения. Хранилища получают уже разобранный запрос — правило нормализации одно для
/// всех видов.
/// </summary>
/// <param name="Raw">Запрос как ввели (обрезан по краям и до <see cref="MaxLength"/>).</param>
/// <param name="Text">Текст для подстрочного поиска; короче <see cref="MinLength"/> — искать нечего.</param>
/// <param name="Plate">Нормализованный госномер (не короче 2 знаков) либо <see langword="null"/>.</param>
/// <param name="Address">Нормализованный адрес (не короче 2 знаков) либо <see langword="null"/>.</param>
/// <param name="Date">Дата «дд.мм.гггг» (или «гггг-мм-дд»), если запрос — дата.</param>
/// <param name="Year">Год (1900–2100), если запрос — четыре цифры.</param>
/// <param name="Phone">
/// Нормализованный номер телефона, если запрос похож на номер: только цифры и знаки «+ - ( ) .» и пробелы, не меньше
/// <see cref="MinPhoneDigits"/> цифр, и это не дата; иначе <see langword="null"/>.
/// </param>
public sealed record SearchTerms(string Raw, string Text, string? Plate, string? Address, DateOnly? Date, int? Year, string? Phone = null)
{
    /// <summary>Наименьшее число цифр запроса, чтобы искать его как номер телефона: короче — слишком много случайных совпадений.</summary>
    public const int MinPhoneDigits = 5;

    /// <summary>Наименьшая длина нормализованного текста запроса.</summary>
    public const int MinLength = 2;

    /// <summary>Наибольшая длина запроса.</summary>
    public const int MaxLength = 200;

    private static readonly string[] DateFormats = ["dd.MM.yyyy", "d.M.yyyy", "yyyy-MM-dd"];

    /// <summary>Есть что искать: нормализованный текст не короче <see cref="MinLength"/>.</summary>
    public bool IsSearchable => Text.Length >= MinLength;

    /// <summary>Разобрать запрос.</summary>
    /// <param name="raw">Запрос как ввели.</param>
    public static SearchTerms Parse(string? raw)
    {
        var trimmed = (raw ?? string.Empty).Trim();
        if (trimmed.Length > MaxLength)
        {
            trimmed = trimmed[..MaxLength];
        }

        var plate = RequisiteNormalizer.Plate(trimmed);
        var address = RequisiteNormalizer.Address(trimmed);
        DateOnly? date = DateOnly.TryParseExact(trimmed, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
        int? year = null;
        if (trimmed.Length == 4 && trimmed.All(char.IsAsciiDigit)
            && int.Parse(trimmed, CultureInfo.InvariantCulture) is >= 1900 and <= 2100 and var parsedYear)
        {
            year = parsedYear;
        }

        return new SearchTerms(
            trimmed,
            RequisiteNormalizer.SearchText(trimmed),
            plate is { Length: >= MinLength } ? plate : null,
            address is { Length: >= MinLength } ? address : null,
            date,
            year,
            date is null && LooksLikePhone(trimmed) ? RequisiteNormalizer.Phone(trimmed) : null);
    }

    // Похоже на номер: только цифры, пробелы и «+ - ( ) .», цифр не меньше MinPhoneDigits.
    private static bool LooksLikePhone(string value) =>
        value.All(c => char.IsAsciiDigit(c) || c is ' ' or '+' or '-' or '(' or ')' or '.')
        && value.Count(char.IsAsciiDigit) >= MinPhoneDigits;
}

/// <summary>Виды выдачи единого поиска (ADR-0048); можно сочетать. Хранится и в истории поиска (ADR-0049).</summary>
[Flags]
public enum SearchKinds
{
    /// <summary>Ничего.</summary>
    None = 0,

    /// <summary>Дела: номер, № задания, название.</summary>
    Cases = 1,

    /// <summary>Фигуранты: ФИО, псевдоним, гражданство, дата или год рождения.</summary>
    Persons = 2,

    /// <summary>Автотранспорт: госномер, марка, модель.</summary>
    Vehicles = 4,

    /// <summary>Адреса фигурантов и место жительства из анкеты.</summary>
    Addresses = 8,

    /// <summary>Сводки и справки: текст полей бланка.</summary>
    Reports = 16,

    /// <summary>Расшифровки речи носителей дел.</summary>
    Transcripts = 32,

    /// <summary>Документы дел: регистрационный номер, вид, краткое содержание.</summary>
    Documents = 64,

    /// <summary>Номера телефонов фигурантов и связей (ADR-0055).</summary>
    Phones = 128,

    /// <summary>
    /// События — «дела-посредники» связи объектов (ADR-0056): название, описание, роль, адрес и госномер контакта
    /// участников с отметкой «в общем поиске».
    /// </summary>
    Events = 256,

    /// <summary>Все виды.</summary>
    All = Cases | Persons | Vehicles | Addresses | Reports | Transcripts | Documents | Phones | Events,
}

/// <summary>Автотранспорт, найденный единым поиском: фигурант (владелец записи) и сама запись.</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="CaseId">Дело фигуранта.</param>
/// <param name="PersonName">ФИО фигуранта.</param>
/// <param name="Vehicle">Запись автотранспорта.</param>
public sealed record VehicleSearchRow(int PersonId, int CaseId, string PersonName, PersonVehicleRow Vehicle);

/// <summary>Адрес, найденный единым поиском: адрес фигуранта либо место жительства из его анкеты.</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="CaseId">Дело фигуранта.</param>
/// <param name="PersonName">ФИО фигуранта.</param>
/// <param name="Kind">Вид адреса; <see langword="null"/> — место жительства из анкеты (ТФ-ПЕР-05).</param>
/// <param name="Text">Адрес как введён.</param>
public sealed record AddressSearchRow(int PersonId, int CaseId, string PersonName, AddressKind? Kind, string Text);

/// <summary>Номер телефона, найденный единым поиском (ADR-0055): фигурант и сама запись номера.</summary>
/// <param name="PersonId">Фигурант.</param>
/// <param name="CaseId">Дело фигуранта.</param>
/// <param name="PersonName">ФИО фигуранта.</param>
/// <param name="Phone">Запись номера.</param>
public sealed record PhoneSearchRow(int PersonId, int CaseId, string PersonName, PersonPhoneRow Phone);

/// <summary>Документ документооборота, привязанный к делу (ТФ-ДЕЛ-02) — для поиска по документам дел.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="DocumentId">Документ документооборота.</param>
public sealed record CaseDocumentRef(int CaseId, int DocumentId);
