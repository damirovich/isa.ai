using System.Globalization;
using System.Text;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Материал дела в корпусе ядра (ADR-0051).</summary>
public enum CaseCorpusKind
{
    /// <summary>«Сведения дела»: реквизиты, фигуранты с анкетами, адреса, транспорт.</summary>
    CaseCard = 1,

    /// <summary>Сводка ОН или справка ОУ по бланку (актуальная редакция действующего документа).</summary>
    Report = 2,
}

/// <summary>Материал дела, текст которого в корпусе.</summary>
/// <param name="Kind">Что за материал.</param>
/// <param name="SourceId">Дело (сведения) или документ по бланку (сводка, справка).</param>
/// <param name="CaseId">Дело.</param>
/// <param name="CoreDocumentId">Документ корпуса ядра.</param>
/// <param name="Label">Подпись источника для ответа.</param>
public sealed record CaseCorpusRef(CaseCorpusKind Kind, int SourceId, int CaseId, int CoreDocumentId, string Label);

/// <summary>
/// Материалы дел профиля в корпусе ядра (ADR-0051): «Сведения дела» и сводки/справки по бланку — для чата и поиска по
/// смыслу наравне с документами и расшифровками.
/// </summary>
/// <remarks>
/// <para>СИНХРОНИЗАЦИЯ ПО ЗАПРОСУ. Перед вопросом к материалам вызывающий синхронизирует дела области: текст строится
/// и кладётся в корпус заново только там, где изменилась отметка источника (дешёвые агрегаты по времени изменений).
/// Писать триггеры на каждую правку фигуранта, адреса или сводки не нужно, и устаревший текст не доживает до ответа.
/// Дела дозаполняются в фоне после старта хоста партиями (<see cref="SyncNextAsync"/>).</para>
/// <para>ИНВАРИАНТЫ. Вызывающий передаёт ТОЛЬКО видимые субъекту дела (решётка — до вызова, ТБ-020/071); гриф и
/// подразделение документа корпуса — дела (ТБ-070). Прежняя версия текста и тексты снятых с действия сводок
/// уничтожаются (ТБ-064), а не остаются «неактуальными». Уничтожение дела — <see cref="PurgeCaseAsync"/> до удаления
/// строк дела.</para>
/// </remarks>
public interface ICaseCorpusSync
{
    /// <summary>Синхронизировать материалы дел и вернуть их документы корпуса.</summary>
    Task<IReadOnlyList<CaseCorpusRef>> SyncAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default);

    /// <summary>Уже положенные в корпус материалы дел — без синхронизации (для строки готовности).</summary>
    Task<IReadOnlyList<CaseCorpusRef>> ListAsync(IReadOnlyCollection<int> caseIds, CancellationToken cancellationToken = default);

    /// <summary>
    /// Синхронизировать следующую партию дел после <paramref name="afterCaseId"/> (фоновое дозаполнение при старте);
    /// последнее обработанное дело или <see langword="null"/> — дел больше нет.
    /// </summary>
    Task<int?> SyncNextAsync(int afterCaseId, CancellationToken cancellationToken = default);

    /// <summary>Уничтожить документы корпуса материалов дела (ТБ-064) — до удаления дела; число уничтоженных.</summary>
    Task<int> PurgeCaseAsync(int caseId, int? subjectId, CancellationToken cancellationToken = default);
}

/// <summary>Реквизиты дела для «Сведений дела».</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Number">Номер.</param>
/// <param name="Title">Название.</param>
/// <param name="Kind">Вид.</param>
/// <param name="Status">Статус.</param>
/// <param name="OpenedAt">Дата заведения.</param>
/// <param name="TaskNumber">№ задания.</param>
/// <param name="Basis">Основание.</param>
/// <param name="Purpose">Цель задания.</param>
public sealed record CaseCardHead(
    int CaseId, string Number, string Title, CaseKind Kind, CaseStatus Status, DateOnly OpenedAt, string? TaskNumber, string? Basis, string? Purpose);

/// <summary>Фигурант для «Сведений дела».</summary>
/// <param name="Name">ФИО или «неустановленное лицо № N».</param>
/// <param name="Alias">Псевдоним.</param>
/// <param name="Role">Роль.</param>
/// <param name="RoleInCase">Роль в деле (текстом).</param>
/// <param name="Sex">Пол.</param>
/// <param name="BirthDate">Дата рождения.</param>
/// <param name="BirthYear">Год рождения.</param>
/// <param name="BirthPlace">Место рождения.</param>
/// <param name="WorkPlace">Место работы.</param>
/// <param name="Residence">Место жительства.</param>
/// <param name="Notes">Примечания.</param>
/// <param name="Addresses">Адреса: вид и текст.</param>
/// <param name="Vehicles">Транспорт текстом (марка, модель, цвет, госномер).</param>
public sealed record CaseCardPerson(
    string Name,
    string? Alias,
    PersonRole Role,
    string? RoleInCase,
    PersonSex? Sex,
    DateOnly? BirthDate,
    int? BirthYear,
    string? BirthPlace,
    string? WorkPlace,
    string? Residence,
    string? Notes,
    IReadOnlyList<(AddressKind Kind, string Text)> Addresses,
    IReadOnlyList<string> Vehicles);

/// <summary>Тексты материалов дела для корпуса (ADR-0051): что и в каком виде читает модель.</summary>
/// <remarks>
/// В шапке каждого текста — номер дела и источника: тексты разных дел с одинаковым содержимым не склеиваются
/// дедупликацией корпуса по хешу, и модель видит, к какому делу относится фрагмент. Каждая запись — отдельный абзац:
/// чанкер режет по абзацам, и фрагмент не обрывается на середине фигуранта или события.
/// </remarks>
public static class CaseCorpusText
{
    /// <summary>Тип документа корпуса «Сведения дела».</summary>
    public const string CardDocType = "Сведения дела";

    /// <summary>Источник документов корпуса профиля.</summary>
    public const string Source = "Следствие";

    /// <summary>Ключ метаданных — дело.</summary>
    public const string CaseIdKey = "investigation_case_id";

    /// <summary>Ключ метаданных — документ по бланку.</summary>
    public const string ReportIdKey = "investigation_report_id";

    /// <summary>Подпись «Сведений дела».</summary>
    public static string CardLabel(string caseNumber) => "Сведения дела № " + caseNumber;

    /// <summary>Подпись сводки или справки.</summary>
    public static string ReportLabel(CaseReportKind kind, DateOnly reportDate, string caseNumber) =>
        string.Create(CultureInfo.InvariantCulture, $"{kind.Label()} от {reportDate:dd.MM.yyyy} · дело № {caseNumber}");

    /// <summary>Текст «Сведений дела».</summary>
    public static string Card(CaseCardHead head, IReadOnlyList<CaseCardPerson> persons)
    {
        ArgumentNullException.ThrowIfNull(head);
        ArgumentNullException.ThrowIfNull(persons);
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(inv, $"Сведения дела № {head.Number} «{head.Title}»: вид — {head.Kind.Label()}, статус — {head.Status.Label()}, заведено {head.OpenedAt:dd.MM.yyyy}.");
        AppendField(text, "№ задания", head.TaskNumber);
        AppendField(text, "Основание", head.Basis);
        AppendField(text, "Цель", head.Purpose);
        if (persons.Count == 0)
        {
            text.Append("\n\nФигурантов в деле нет.");
        }

        foreach (var person in persons)
        {
            text.Append("\n\n").Append(inv, $"Фигурант дела № {head.Number}: {person.Name}");
            if (!string.IsNullOrWhiteSpace(person.Alias))
            {
                text.Append(inv, $" (псевдоним «{person.Alias.Trim()}»)");
            }

            text.Append(inv, $"; роль — {person.Role.Label()}");
            AppendPart(text, "роль в деле", person.RoleInCase);
            if (person.Sex is { } sex)
            {
                text.Append("; пол — ").Append(sex.Label().ToLowerInvariant());
            }

            if (person.BirthDate is { } birthDate)
            {
                text.Append(inv, $"; дата рождения {birthDate:dd.MM.yyyy}");
            }
            else if (person.BirthYear is { } birthYear)
            {
                text.Append(inv, $"; год рождения {birthYear}");
            }

            AppendPart(text, "место рождения", person.BirthPlace);
            AppendPart(text, "место работы", person.WorkPlace);
            AppendPart(text, "место жительства", person.Residence);
            foreach (var (kind, address) in person.Addresses)
            {
                AppendPart(text, "адрес (" + kind.Label().ToLowerInvariant() + ")", address);
            }

            foreach (var vehicle in person.Vehicles)
            {
                AppendPart(text, "транспорт", vehicle);
            }

            AppendPart(text, "примечание", person.Notes);
            text.Append('.');
        }

        return text.ToString();
    }

    /// <summary>Текст сводки или справки (актуальная редакция).</summary>
    public static string Report(
        int reportId, string caseNumber, CaseReportKind kind, DateOnly reportDate, string? personName, int revision, CaseReportContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        var inv = CultureInfo.InvariantCulture;
        var text = new StringBuilder();
        text.Append(inv, $"{kind.Label()} от {reportDate:dd.MM.yyyy} по делу № {caseNumber} (документ № {reportId}, редакция {revision})");
        if (!string.IsNullOrWhiteSpace(personName))
        {
            text.Append(inv, $", объект: {personName}");
        }

        text.Append('.');
        if (kind == CaseReportKind.SummaryOn)
        {
            foreach (var row in content.EventRows)
            {
                var line = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(row.Time))
                {
                    line.Append(row.Time.Trim()).Append(" — ");
                }

                line.Append(string.Join("; ", new[] { row.Place, row.Description }.Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!.Trim())));
                AppendPart(line, "лица", row.Persons);
                AppendPart(line, "транспорт", row.Vehicles);
                text.Append("\n\nСобытие: ").Append(line.ToString().Trim()).Append('.');
            }
        }
        else
        {
            AppendSection(text, "Установочные данные", content.Identity);
            AppendSection(text, "Адреса", content.Addresses);
            AppendSection(text, "Род занятий", content.Occupation);
            AppendSection(text, "Семейные и иные связи", content.Family);
            AppendSection(text, "Характеризующие материалы", content.Characterizing);
        }

        AppendSection(text, "Вывод", content.Conclusion);
        return text.ToString();
    }

    private static void AppendField(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append(' ').Append(name).Append(": ").Append(value.Trim()).Append('.');
        }
    }

    private static void AppendPart(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append("; ").Append(name).Append(" — ").Append(value.Trim());
        }
    }

    private static void AppendSection(StringBuilder text, string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            text.Append("\n\n").Append(name).Append(": ").Append(value.Trim());
        }
    }
}
