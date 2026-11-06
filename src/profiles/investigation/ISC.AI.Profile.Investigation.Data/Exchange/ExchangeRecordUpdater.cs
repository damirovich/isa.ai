using System.Globalization;
using System.Text.Json;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>Трёхстороннее сравнение одного поля (ADR-0068 v0.4): снимок, источник, получатель.</summary>
public static class ExchangeThreeWay
{
    /// <summary>Что делать с полем.</summary>
    public enum Action
    {
        /// <summary>У источника поле не менялось — ничего.</summary>
        Keep = 0,

        /// <summary>Изменено у источника, у получателя не трогали — записать значение источника.</summary>
        Apply = 1,

        /// <summary>У обоих одно и то же новое значение — только снимок.</summary>
        Converged = 2,

        /// <summary>Изменено у обоих по-разному — конфликт изменений.</summary>
        Conflict = 3,
    }

    /// <summary>Решение по полю: <paramref name="baseValue"/> — снимок, с которым запись последний раз сведена.</summary>
    public static Action Decide(string? baseValue, string? source, string? local) =>
        string.Equals(source, baseValue, StringComparison.Ordinal) ? Action.Keep
        : string.Equals(local, baseValue, StringComparison.Ordinal) ? Action.Apply
        : string.Equals(local, source, StringComparison.Ordinal) ? Action.Converged
        : Action.Conflict;
}

/// <summary>Исход обновления принятой записи (ADR-0068 v0.4).</summary>
public enum ExchangeUpdateStatus
{
    /// <summary>Ничего не изменилось (или снимок только запомнен).</summary>
    Unchanged = 0,

    /// <summary>Изменения источника записаны.</summary>
    Updated = 1,

    /// <summary>Есть конфликт изменений — ждёт Руководителя (остальные изменённые поля записаны).</summary>
    Conflict = 2,
}

/// <summary>Итог обновления одной записи: исход, запись словами, поля записанные и конфликтные (ключи полей).</summary>
/// <param name="Status">Исход.</param>
/// <param name="Label">Запись словами (своя) — для акта.</param>
/// <param name="Applied">Записанные поля.</param>
/// <param name="Conflicts">Конфликтные поля.</param>
/// <param name="Summary">Что изменено словами — в защищённую часть акта.</param>
public sealed record ExchangeUpdateResult(
    ExchangeUpdateStatus Status, string Label, IReadOnlyList<string> Applied, IReadOnlyList<string> Conflicts, string? Summary)
{
    /// <summary>Записи нет или не изменилась.</summary>
    public static ExchangeUpdateResult None { get; } = new(ExchangeUpdateStatus.Unchanged, string.Empty, [], [], null);

    /// <summary>Поле записано или ушло конфликтом.</summary>
    public bool Touched(string key) => Applied.Contains(key) || Conflicts.Contains(key);
}

/// <summary>
/// Обстановка обновления в одном приёме (ADR-0068 v0.4): сопоставления справочника, сотрудников и объектов, окончание
/// прежних приёмов, объединённые дела и имена для текста конфликтов.
/// </summary>
public sealed class ExchangeUpdateContext
{
    /// <summary>Экземпляр-источник.</summary>
    public required string Source { get; init; }

    /// <summary>Приём.</summary>
    public required int ImportId { get; init; }

    /// <summary>Пометка новой редакции сводки «изменено у источника».</summary>
    public required string Note { get; init; }

    /// <summary>Сотрудники: номер источника → своя учётка.</summary>
    public required IReadOnlyDictionary<int, int> Users { get; init; }

    /// <summary>Справочник: номер источника → своя запись.</summary>
    public required IReadOnlyDictionary<int, int> References { get; init; }

    /// <summary>Объекты: номер источника → свой (дополняется по ходу приёма).</summary>
    public required IReadOnlyDictionary<int, int> Persons { get; init; }

    /// <summary>Когда закончился каждый прежний приём (UTC).</summary>
    public required IReadOnlyDictionary<int, DateTime?> ImportFinished { get; init; }

    /// <summary>Дела источника, объединённые со своими по решению Руководителя (применённому).</summary>
    public required IReadOnlySet<int> MergedCases { get; init; }

    /// <summary>Имена для текста конфликтов.</summary>
    public required ExchangeNames Names { get; init; }

    /// <summary>Момент приёма (UTC).</summary>
    public required DateTime Now { get; init; }

    /// <summary>Своя учётка сотрудника источника или <see langword="null"/>.</summary>
    public int? User(int? sourceId) => sourceId is { } id && Users.TryGetValue(id, out var local) ? local : null;

    /// <summary>Своя запись справочника или <see langword="null"/>.</summary>
    public int? Reference(int? sourceId) => sourceId is { } id && References.TryGetValue(id, out var local) ? local : null;

    /// <summary>Свой объект или <see langword="null"/>.</summary>
    public int? Person(int? sourceId) => sourceId is { } id && Persons.TryGetValue(id, out var local) ? local : null;
}

/// <summary>Имена учёток, записей справочника и объектов — для текста конфликтов изменений.</summary>
public sealed class ExchangeNames
{
    /// <summary>Учётки: номер → ФИО или имя входа.</summary>
    public Dictionary<int, string> Users { get; } = [];

    /// <summary>Справочник: номер → название.</summary>
    public Dictionary<int, string> References { get; } = [];

    /// <summary>Объекты: номер → ФИО.</summary>
    public Dictionary<int, string> Persons { get; } = [];
}

/// <summary>Обстановка записи значения: база (новая редакция сводки), пометка, автор редакции.</summary>
/// <param name="Db">Контекст.</param>
/// <param name="Note">Пометка новой редакции сводки.</param>
/// <param name="AuthorUserId">Автор новой редакции.</param>
public sealed record ExchangeApply(InvestigationDbContext Db, string? Note, int? AuthorUserId);

/// <summary>Сводка с содержимым действующей редакции — содержимое живёт в редакциях.</summary>
/// <param name="report">Сводка.</param>
/// <param name="content">Содержимое действующей редакции (JSON).</param>
public sealed class ExchangeReportState(CaseReport report, string? content)
{
    /// <summary>Сводка.</summary>
    public CaseReport Report { get; } = report;

    /// <summary>Содержимое действующей редакции (JSON).</summary>
    public string? Content { get; set; } = content;
}

/// <summary>
/// Перенос изменений уже принятых записей (ТФ-ОБМ-02, ADR-0068 v0.4): по каждому полю — снимок (каким поле было у источника
/// при последнем сведении), источник (как теперь у источника), получатель (как теперь у себя). Изменение источника
/// записывается, если поле у получателя не трогали; изменённое у обоих по-разному — конфликт изменений для Руководителя.
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТ (ADR-0068 v0.4): правка получателя молча не затирается — поле, изменённое у получателя, значение источника
/// перезаписывает только решением Руководителя «принять версию источника». Гриф — только вверх: приём гриф не понижает
/// (ТБ-070).
/// </para>
/// <para>
/// Значения сравниваются в виде получателя: строки — без крайних пробелов, пустая строка = нет значения; номера справочника,
/// учёток и объектов — свои; перечисления — числом; даты — ISO; содержимое сводки — тем же JSON, что пишет ввод. Связанные
/// поля (вид дела с реквизитами задания, роль объекта со связью, дата рождения с годом) сравниваются и записываются одним
/// полем — их держит одно правило таблицы. Снимок — значения ИСТОЧНИКА: свои отличия, сделанные приёмом намеренно (новый
/// номер дела по решению Руководителя, связь с непринятым объектом), изменением источника не считаются.
/// </para>
/// </remarks>
public static class ExchangeRecordUpdater
{
    private static readonly JsonSerializerOptions Json = new();

    // ---------- поля по видам записей ----------

    private static readonly IReadOnlyList<UpdateField<CaseFile>> CaseFields =
    [
        new("number", "номер", c => ExchangeCanon.Text(c.Number), (c, v, _) => c.Number = v ?? c.Number, Plain),
        new("title", "наименование", c => ExchangeCanon.Text(c.Title), (c, v, _) => c.Title = v ?? c.Title, Plain),
        new("kindTask", "вид дела и реквизиты задания",
            c => ExchangeCanon.Compose(ExchangeCanon.Enum(c.Kind), ExchangeCanon.Text(c.TaskNumber), ExchangeCanon.Int(c.InitiatorUnitId),
                ExchangeCanon.Text(c.InitiatorName), ExchangeCanon.Int(c.InitiatorRankId), ExchangeCanon.Int(c.InitiatorPositionId),
                ExchangeCanon.Text(c.InitiatorPhone), ExchangeCanon.Text(c.InitiatorDetails), ExchangeCanon.Text(c.Justification),
                ExchangeCanon.Text(c.Purpose), ExchangeCanon.Text(c.TaskNotes)),
            (c, v, _) =>
            {
                var p = ExchangeCanon.Split(v, 11);
                c.Kind = (CaseKind)(ExchangeCanon.ToInt(p[0]) ?? (int)c.Kind);
                c.TaskNumber = p[1];
                c.InitiatorUnitId = ExchangeCanon.ToInt(p[2]);
                c.InitiatorName = p[3];
                c.InitiatorRankId = ExchangeCanon.ToInt(p[4]);
                c.InitiatorPositionId = ExchangeCanon.ToInt(p[5]);
                c.InitiatorPhone = p[6];
                c.InitiatorDetails = p[7];
                c.Justification = p[8];
                c.Purpose = p[9];
                c.TaskNotes = p[10];
            },
            (v, n) =>
            {
                var p = ExchangeCanon.Split(v, 11);
                var kind = ExchangeCanon.ToInt(p[0]) is { } k ? ((CaseKind)k).Label() : "—";
                return p[1] is null && p[2] is null ? kind : kind + ": " + Name(n.References, p[2]) + " / № " + (p[1] ?? "—");
            }),
        new("opened", "дата заведения", c => ExchangeCanon.Date(c.OpenedAt), (c, v, _) => c.OpenedAt = ExchangeCanon.ToDate(v) ?? c.OpenedAt, DateText),
        new("status", "статус", c => ExchangeCanon.Enum(c.Status), (c, v, _) => c.Status = (CaseStatus)(ExchangeCanon.ToInt(v) ?? (int)c.Status),
            (v, _) => ExchangeCanon.ToInt(v) is { } s ? ((CaseStatus)s).Label() : null),
        new("closed", "дата закрытия", c => ExchangeCanon.Moment(c.ClosedAt), (c, v, _) => c.ClosedAt = ExchangeCanon.ToMoment(v), MomentText),
        new("basis", "основание", c => ExchangeCanon.Text(c.Basis), (c, v, _) => c.Basis = v, Plain),
        new("investigator", "ведущий", c => ExchangeCanon.Int(c.InvestigatorUserId), (c, v, _) => c.InvestigatorUserId = ExchangeCanon.ToInt(v),
            (v, n) => v is null ? null : Name(n.Users, v)),
        new("classification", "гриф", c => ExchangeCanon.Int(c.Classification),
            (c, v, _) => c.Classification = ExchangeCanon.ToShort(v) ?? c.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<Person>> PersonFields =
    [
        new("identity", "ФИО", p => ExchangeCanon.Compose(ExchangeCanon.Bool(p.IsUnidentified), ExchangeCanon.Text(p.DisplayName)),
            (p, v, _) =>
            {
                // Номер неустановленного лица остаётся (как при ручной правке, PersonStore); недостающий выдаст проверка
                // записи. Нормализация ФИО — та же, что у ввода: пересечения и поиск видят изменённое так же.
                var x = ExchangeCanon.Split(v, 2);
                p.IsUnidentified = x[0] == "1";
                p.DisplayName = x[1] ?? p.DisplayName;
                p.NameNormalized = p.IsUnidentified ? null : RequisiteNormalizer.PersonName(p.DisplayName);
            },
            (v, _) =>
            {
                var x = ExchangeCanon.Split(v, 2);
                return (x[1] ?? "—") + (x[0] == "1" ? " (не установлен)" : string.Empty);
            }),
        new("role", "роль и связь",
            p => ExchangeCanon.Compose(ExchangeCanon.Enum(p.Role), ExchangeCanon.Int(p.LinkedToPersonId), ExchangeCanon.Int(p.LinkTypeId)),
            (p, v, _) =>
            {
                var x = ExchangeCanon.Split(v, 3);
                p.Role = (PersonRole)(ExchangeCanon.ToInt(x[0]) ?? (int)p.Role);
                p.LinkedToPersonId = p.Role == PersonRole.Link ? ExchangeCanon.ToInt(x[1]) : null;
                p.LinkTypeId = p.Role == PersonRole.Link ? ExchangeCanon.ToInt(x[2]) : null;
            },
            (v, n) =>
            {
                var x = ExchangeCanon.Split(v, 3);
                var role = ExchangeCanon.ToInt(x[0]) is { } r ? ((PersonRole)r).Label() : "—";
                return x[1] is null
                    ? role
                    : role + ": " + Name(n.Persons, x[1]) + (x[2] is null ? string.Empty : " (" + Name(n.References, x[2]) + ")");
            }),
        new("roleInCase", "роль в деле", p => ExchangeCanon.Text(p.RoleInCase), (p, v, _) => p.RoleInCase = v, Plain),
        new("birth", "рождение", p => ExchangeCanon.Compose(ExchangeCanon.Date(p.BirthDate), ExchangeCanon.Int(p.BirthYear)),
            (p, v, _) =>
            {
                var x = ExchangeCanon.Split(v, 2);
                p.BirthDate = ExchangeCanon.ToDate(x[0]);
                p.BirthYear = p.BirthDate?.Year ?? ExchangeCanon.ToInt(x[1]);
            },
            (v, _) =>
            {
                var x = ExchangeCanon.Split(v, 2);
                return ExchangeCanon.ToDate(x[0]) is { } date
                    ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture)
                    : x[1] is null ? null : x[1] + " г.";
            }),
        new("birthPlace", "место рождения", p => ExchangeCanon.Text(p.BirthPlace), (p, v, _) => p.BirthPlace = v, Plain),
        new("workPlace", "место работы", p => ExchangeCanon.Text(p.WorkPlace), (p, v, _) => p.WorkPlace = v, Plain),
        new("residence", "место жительства", p => ExchangeCanon.Text(p.Residence),
            (p, v, _) =>
            {
                p.Residence = v;
                p.ResidenceNormalized = RequisiteNormalizer.Address(v);
            },
            Plain),
        new("sex", "пол", p => p.Sex is { } s ? ExchangeCanon.Enum(s) : null,
            (p, v, _) => p.Sex = ExchangeCanon.ToInt(v) is { } s ? (PersonSex)s : null,
            (v, _) => ExchangeCanon.ToInt(v) is { } s ? ((PersonSex)s).Label() : null),
        new("alias", "псевдоним", p => ExchangeCanon.Text(p.Alias), (p, v, _) => p.Alias = v, Plain),
        new("citizenship", "гражданство", p => ExchangeCanon.Text(p.Citizenship), (p, v, _) => p.Citizenship = v, Plain),
        new("orientation", "ориентировка", p => ExchangeCanon.Text(p.Orientation), (p, v, _) => p.Orientation = v, Plain),
        new("notes", "примечания", p => ExchangeCanon.Text(p.Notes), (p, v, _) => p.Notes = v, Plain),
        new("classification", "гриф", p => ExchangeCanon.Int(p.Classification),
            (p, v, _) => p.Classification = ExchangeCanon.ToShort(v) ?? p.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<PersonAddress>> AddressFields =
    [
        new("kind", "вид адреса", a => ExchangeCanon.Enum(a.Kind), (a, v, _) => a.Kind = (AddressKind)(ExchangeCanon.ToInt(v) ?? (int)a.Kind),
            (v, _) => ExchangeCanon.ToInt(v) is { } k ? ((AddressKind)k).Label() : null),
        new("text", "адрес", a => ExchangeCanon.Text(a.Text),
            (a, v, _) =>
            {
                a.Text = v ?? a.Text;
                a.TextNormalized = RequisiteNormalizer.Address(a.Text) ?? string.Empty;
            },
            Plain),
        new("notes", "примечания", a => ExchangeCanon.Text(a.Notes), (a, v, _) => a.Notes = v, Plain),
        new("classification", "гриф", a => ExchangeCanon.Int(a.Classification),
            (a, v, _) => a.Classification = ExchangeCanon.ToShort(v) ?? a.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<PersonPhone>> PhoneFields =
    [
        new("kind", "вид телефона", p => ExchangeCanon.Enum(p.Kind), (p, v, _) => p.Kind = (PhoneKind)(ExchangeCanon.ToInt(v) ?? (int)p.Kind),
            (v, _) => ExchangeCanon.ToInt(v) is { } k ? ((PhoneKind)k).Label() : null),
        new("number", "номер", p => ExchangeCanon.Text(p.Number),
            (p, v, _) =>
            {
                // Номер без цифр таблица не примет (ck_person_phone_normalized_digits) — такой не записывается.
                if (v is not null && RequisiteNormalizer.Phone(v) is { } normalized)
                {
                    p.Number = v;
                    p.NumberNormalized = normalized;
                }
            },
            Plain),
        new("notes", "примечания", p => ExchangeCanon.Text(p.Notes), (p, v, _) => p.Notes = v, Plain),
        new("classification", "гриф", p => ExchangeCanon.Int(p.Classification),
            (p, v, _) => p.Classification = ExchangeCanon.ToShort(v) ?? p.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<PersonVehicle>> VehicleFields =
    [
        new("plate", "госномер", t => ExchangeCanon.Text(t.PlateNumber),
            (t, v, _) =>
            {
                t.PlateNumber = v;
                t.PlateNormalized = RequisiteNormalizer.Plate(v);
            },
            Plain),
        new("make", "марка", t => ExchangeCanon.Text(t.Make), (t, v, _) => t.Make = v, Plain),
        new("model", "модель", t => ExchangeCanon.Text(t.Model), (t, v, _) => t.Model = v, Plain),
        new("color", "цвет", t => ExchangeCanon.Text(t.Color), (t, v, _) => t.Color = v, Plain),
        new("notes", "примечания", t => ExchangeCanon.Text(t.Notes), (t, v, _) => t.Notes = v, Plain),
        new("classification", "гриф", t => ExchangeCanon.Int(t.Classification),
            (t, v, _) => t.Classification = ExchangeCanon.ToShort(v) ?? t.Classification, ClassificationText, RaiseOnly: true),
    ];

    // Вид сводки — раньше содержимого: текст для поиска считается по виду.
    private static readonly IReadOnlyList<UpdateField<ExchangeReportState>> ReportFields =
    [
        new("kind", "вид", r => ExchangeCanon.Enum(r.Report.Kind),
            (r, v, _) => r.Report.Kind = (CaseReportKind)(ExchangeCanon.ToInt(v) ?? (int)r.Report.Kind),
            (v, _) => ExchangeCanon.ToInt(v) is { } k ? ((CaseReportKind)k).Label() : null),
        new("date", "дата", r => ExchangeCanon.Date(r.Report.ReportDate),
            (r, v, _) => r.Report.ReportDate = ExchangeCanon.ToDate(v) ?? r.Report.ReportDate, DateText),
        new("active", "действует", r => ExchangeCanon.Bool(r.Report.IsActive), (r, v, _) => r.Report.IsActive = v == "1", BoolText),
        new("person", "объект", r => ExchangeCanon.Int(r.Report.PersonId), (r, v, _) => r.Report.PersonId = ExchangeCanon.ToInt(v),
            (v, n) => v is null ? null : Name(n.Persons, v)),
        new("content", "содержимое", r => ExchangeCanon.Report(r.Content),
            (r, v, apply) =>
            {
                // Новая редакция у получателя — прежние остаются в истории (ТФ-ДДЛ-05): изменение источника их не затирает.
                var report = r.Report;
                var number = report.CurrentRevision + 1;
                apply.Db.CaseReportRevisions.Add(new CaseReportRevision
                {
                    ReportId = report.Id,
                    Number = number,
                    ContentJson = v ?? CaseReportContent.Empty.ToJson(),
                    AuthorUserId = apply.AuthorUserId,
                    EditReason = apply.Note,
                    Classification = report.Classification,
                    DivisionId = report.DivisionId,
                });
                report.CurrentRevision = number;
                report.SearchText = CaseReportContent.FromJson(v).SearchText(report.Kind);
                r.Content = v;
            },
            (v, _) => v is null ? null : ReportText(v)),
        new("classification", "гриф", r => ExchangeCanon.Int(r.Report.Classification),
            (r, v, _) => r.Report.Classification = ExchangeCanon.ToShort(v) ?? r.Report.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<LinkEvent>> EventFields =
    [
        new("kind", "вид", e => ExchangeCanon.Enum(e.Kind), (e, v, _) => e.Kind = (LinkEventKind)(ExchangeCanon.ToInt(v) ?? (int)e.Kind),
            (v, _) => ExchangeCanon.ToInt(v) is { } k ? ((LinkEventKind)k).Label() : null),
        new("title", "заголовок", e => ExchangeCanon.Text(e.Title), (e, v, _) => e.Title = v ?? e.Title, Plain),
        new("occurred", "дата", e => ExchangeCanon.Date(e.OccurredOn), (e, v, _) => e.OccurredOn = ExchangeCanon.ToDate(v), DateText),
        new("description", "описание", e => ExchangeCanon.Text(e.Description), (e, v, _) => e.Description = v, Plain),
        new("classification", "гриф", e => ExchangeCanon.Int(e.Classification),
            (e, v, _) => e.Classification = ExchangeCanon.ToShort(v) ?? e.Classification, ClassificationText, RaiseOnly: true),
    ];

    private static readonly IReadOnlyList<UpdateField<LinkEventParticipant>> ParticipantFields =
    [
        new("role", "роль в событии", p => ExchangeCanon.Text(p.Role), (p, v, _) => p.Role = v, Plain),
        new("level", "уровень связи", p => ExchangeCanon.Enum(p.Level), (p, v, _) => p.Level = (LinkLevel)(ExchangeCanon.ToInt(v) ?? (int)p.Level),
            (v, _) => ExchangeCanon.ToInt(v) is { } l ? ((LinkLevel)l).Label() : null),
        new("inSearch", "в розыске", p => ExchangeCanon.Bool(p.InSearch), (p, v, _) => p.InSearch = v == "1", BoolText),
        new("vehiclePlate", "госномер", p => ExchangeCanon.Text(p.VehiclePlate),
            (p, v, _) =>
            {
                p.VehiclePlate = v;
                p.VehiclePlateNormalized = RequisiteNormalizer.Plate(v);
            },
            Plain),
        new("vehicleDetails", "транспорт", p => ExchangeCanon.Text(p.VehicleDetails), (p, v, _) => p.VehicleDetails = v, Plain),
        new("contactAddress", "адрес встречи", p => ExchangeCanon.Text(p.ContactAddress),
            (p, v, _) =>
            {
                p.ContactAddress = v;
                p.ContactAddressNormalized = RequisiteNormalizer.Address(v);
            },
            Plain),
        new("classification", "гриф", p => ExchangeCanon.Int(p.Classification),
            (p, v, _) => p.Classification = ExchangeCanon.ToShort(v) ?? p.Classification, ClassificationText, RaiseOnly: true),
    ];

    // ---------- значения источника в виде получателя ----------

    private static Dictionary<string, string?> SourceOf(ExchangeCase s, ExchangeUpdateContext ctx)
    {
        var t = s.Task;
        return new(StringComparer.Ordinal)
        {
            ["number"] = ExchangeCanon.Text(s.Number),
            ["title"] = ExchangeCanon.Text(s.Title),
            ["kindTask"] = ExchangeCanon.Compose(ExchangeCanon.Enum(s.Kind), ExchangeCanon.Text(t?.TaskNumber),
                ExchangeCanon.Int(ctx.Reference(t?.InitiatorUnitSourceId)), ExchangeCanon.Text(t?.InitiatorName),
                ExchangeCanon.Int(ctx.Reference(t?.InitiatorRankSourceId)), ExchangeCanon.Int(ctx.Reference(t?.InitiatorPositionSourceId)),
                ExchangeCanon.Text(t?.InitiatorPhone), ExchangeCanon.Text(t?.InitiatorDetails), ExchangeCanon.Text(t?.Justification),
                ExchangeCanon.Text(t?.Purpose), ExchangeCanon.Text(t?.TaskNotes)),
            ["opened"] = ExchangeCanon.Date(s.OpenedAt),
            ["status"] = ExchangeCanon.Enum(s.Status),
            ["closed"] = ExchangeCanon.Moment(s.ClosedAt),
            ["basis"] = ExchangeCanon.Text(s.Basis),
            ["investigator"] = ExchangeCanon.Int(ctx.User(s.InvestigatorUserSourceId)),
            ["classification"] = ExchangeCanon.Int(s.Classification),
        };
    }

    // Связь — только у роли «связь» и только с объектом, принятым у получателя (как при заведении записи).
    private static Dictionary<string, string?> SourceOf(ExchangePerson s, ExchangeUpdateContext ctx)
    {
        var linked = s.Role == PersonRole.Link ? ctx.Person(s.LinkedToPersonSourceId) : null;
        return new(StringComparer.Ordinal)
        {
            ["identity"] = ExchangeCanon.Compose(ExchangeCanon.Bool(s.IsUnidentified), ExchangeCanon.Text(s.DisplayName)),
            ["role"] = ExchangeCanon.Compose(ExchangeCanon.Enum(s.Role), ExchangeCanon.Int(linked),
                ExchangeCanon.Int(linked is null ? null : ctx.Reference(s.LinkTypeSourceId))),
            ["roleInCase"] = ExchangeCanon.Text(s.RoleInCase),
            ["birth"] = ExchangeCanon.Compose(ExchangeCanon.Date(s.BirthDate), ExchangeCanon.Int(s.BirthDate?.Year ?? s.BirthYear)),
            ["birthPlace"] = ExchangeCanon.Text(s.BirthPlace),
            ["workPlace"] = ExchangeCanon.Text(s.WorkPlace),
            ["residence"] = ExchangeCanon.Text(s.Residence),
            ["sex"] = s.Sex is { } sex ? ExchangeCanon.Enum(sex) : null,
            ["alias"] = ExchangeCanon.Text(s.Alias),
            ["citizenship"] = ExchangeCanon.Text(s.Citizenship),
            ["orientation"] = ExchangeCanon.Text(s.Orientation),
            ["notes"] = ExchangeCanon.Text(s.Notes),
            ["classification"] = ExchangeCanon.Int(s.Classification),
        };
    }

    private static Dictionary<string, string?> SourceOf(ExchangeAddress s) => new(StringComparer.Ordinal)
    {
        ["kind"] = ExchangeCanon.Enum(s.Kind),
        ["text"] = ExchangeCanon.Text(s.Text),
        ["notes"] = ExchangeCanon.Text(s.Notes),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    private static Dictionary<string, string?> SourceOf(ExchangePhone s) => new(StringComparer.Ordinal)
    {
        ["kind"] = ExchangeCanon.Enum(s.Kind),
        ["number"] = ExchangeCanon.Text(s.Number),
        ["notes"] = ExchangeCanon.Text(s.Notes),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    private static Dictionary<string, string?> SourceOf(ExchangeVehicle s) => new(StringComparer.Ordinal)
    {
        ["plate"] = ExchangeCanon.Text(s.PlateNumber),
        ["make"] = ExchangeCanon.Text(s.Make),
        ["model"] = ExchangeCanon.Text(s.Model),
        ["color"] = ExchangeCanon.Text(s.Color),
        ["notes"] = ExchangeCanon.Text(s.Notes),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    // Содержимое — той же нормализацией, что при заведении сводки приёмом и при вводе (CaseReportStore).
    private static Dictionary<string, string?> SourceOf(ExchangeReport s, ExchangeUpdateContext ctx) => new(StringComparer.Ordinal)
    {
        ["kind"] = ExchangeCanon.Enum(s.Kind),
        ["date"] = ExchangeCanon.Date(s.ReportDate),
        ["active"] = ExchangeCanon.Bool(s.IsActive),
        ["person"] = ExchangeCanon.Int(ctx.Person(s.PersonSourceId)),
        ["content"] = CaseReportContent.FromJson(s.ContentJson).Normalize(s.Kind).ToJson(),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    private static Dictionary<string, string?> SourceOf(ExchangeLinkEvent s) => new(StringComparer.Ordinal)
    {
        ["kind"] = ExchangeCanon.Enum(s.Kind),
        ["title"] = ExchangeCanon.Text(s.Title),
        ["occurred"] = ExchangeCanon.Date(s.OccurredOn),
        ["description"] = ExchangeCanon.Text(s.Description),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    private static Dictionary<string, string?> SourceOf(ExchangeParticipant s) => new(StringComparer.Ordinal)
    {
        ["role"] = ExchangeCanon.Text(s.Role),
        ["level"] = ExchangeCanon.Enum(s.Level),
        ["inSearch"] = ExchangeCanon.Bool(s.InSearch),
        ["vehiclePlate"] = ExchangeCanon.Text(s.VehiclePlate),
        ["vehicleDetails"] = ExchangeCanon.Text(s.VehicleDetails),
        ["contactAddress"] = ExchangeCanon.Text(s.ContactAddress),
        ["classification"] = ExchangeCanon.Int(s.Classification),
    };

    // ---------- снимки для новых строк карты (значения источника) ----------

    /// <summary>Снимок дела источника — при заведении и при объединении со своим делом.</summary>
    public static string SnapshotOf(ExchangeCase source, ExchangeUpdateContext ctx)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ctx);
        return Serialize(SourceOf(source, ctx));
    }

    /// <summary>Снимок объекта источника (связь — по объектам, уже сопоставленным в этом приёме).</summary>
    public static string SnapshotOf(ExchangePerson source, ExchangeUpdateContext ctx)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ctx);
        return Serialize(SourceOf(source, ctx));
    }

    /// <summary>Снимок адреса источника.</summary>
    public static string SnapshotOf(ExchangeAddress source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Serialize(SourceOf(source));
    }

    /// <summary>Снимок телефона источника.</summary>
    public static string SnapshotOf(ExchangePhone source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Serialize(SourceOf(source));
    }

    /// <summary>Снимок транспорта источника.</summary>
    public static string SnapshotOf(ExchangeVehicle source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Serialize(SourceOf(source));
    }

    /// <summary>Снимок сводки источника (содержимое действующей редакции).</summary>
    public static string SnapshotOf(ExchangeReport source, ExchangeUpdateContext ctx)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(ctx);
        return Serialize(SourceOf(source, ctx));
    }

    /// <summary>Снимок события связи источника.</summary>
    public static string SnapshotOf(ExchangeLinkEvent source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Serialize(SourceOf(source));
    }

    /// <summary>Снимок участника события связи источника.</summary>
    public static string SnapshotOf(ExchangeParticipant source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return Serialize(SourceOf(source));
    }

    // ---------- обновление принятых записей ----------

    /// <summary>Обновляет принятое прежде дело по пакету (подразделение не меняется — его решает получатель).</summary>
    public static async Task<ExchangeUpdateResult> UpdateCaseAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeCase source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.Cases.FirstOrDefaultAsync(c => c.Id == localId, cancellationToken);
        if (local is null)
        {
            return ExchangeUpdateResult.None;
        }

        return await UpdateAsync(
            db, ctx, ExchangeRecordKind.Case, source.SourceId, local, local, local.UpdatedAt, CaseFields, SourceOf(source, ctx),
            "дело № " + local.Number, ctx.MergedCases.Contains(source.SourceId), authorUserId: null,
            async (merged, ct) =>
            {
                // Номер дела в подразделении уникален: занятый номер не записать — это конфликт, а не сбой приёма.
                if (merged.WasApplied("number")
                    && await db.Cases.AnyAsync(c => c.Id != local.Id && c.DivisionId == local.DivisionId && c.Number == local.Number, ct))
                {
                    merged.Revert(local, "number");
                }
            },
            cancellationToken);
    }

    /// <summary>Обновляет принятый прежде объект (связь — с объектами, уже сопоставленными в этом приёме и раньше).</summary>
    public static async Task<ExchangeUpdateResult> UpdatePersonAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangePerson source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.Persons.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken);
        if (local is null)
        {
            return ExchangeUpdateResult.None;
        }

        await RememberPersonsAsync(db, ctx, local.CaseId, cancellationToken);
        var number = await CaseNumberAsync(db, local.CaseId, cancellationToken);
        return await UpdateAsync(
            db, ctx, ExchangeRecordKind.Person, source.SourceId, local, local, local.UpdatedAt, PersonFields, SourceOf(source, ctx),
            "объект «" + local.DisplayName + "» (дело № " + number + ")", merged: false, authorUserId: null,
            (_, ct) => EnsureUnidentifiedNumberAsync(db, local, ct),
            cancellationToken);
    }

    /// <summary>Обновляет принятый прежде адрес объекта.</summary>
    public static async Task<ExchangeUpdateResult> UpdateAddressAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeAddress source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.PersonAddresses.FirstOrDefaultAsync(a => a.Id == localId, cancellationToken);
        return local is null
            ? ExchangeUpdateResult.None
            : await UpdateAsync(
                db, ctx, ExchangeRecordKind.Address, source.SourceId, local, local, local.UpdatedAt, AddressFields, SourceOf(source),
                "адрес объекта «" + await PersonNameAsync(db, local.PersonId, cancellationToken) + "»", merged: false, authorUserId: null,
                check: null, cancellationToken);
    }

    /// <summary>Обновляет принятый прежде телефон объекта.</summary>
    public static async Task<ExchangeUpdateResult> UpdatePhoneAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangePhone source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.PersonPhones.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken);
        return local is null
            ? ExchangeUpdateResult.None
            : await UpdateAsync(
                db, ctx, ExchangeRecordKind.Phone, source.SourceId, local, local, local.UpdatedAt, PhoneFields, SourceOf(source),
                "телефон объекта «" + await PersonNameAsync(db, local.PersonId, cancellationToken) + "»", merged: false, authorUserId: null,
                check: null, cancellationToken);
    }

    /// <summary>Обновляет принятый прежде транспорт объекта.</summary>
    public static async Task<ExchangeUpdateResult> UpdateVehicleAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeVehicle source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.PersonVehicles.FirstOrDefaultAsync(v => v.Id == localId, cancellationToken);
        return local is null
            ? ExchangeUpdateResult.None
            : await UpdateAsync(
                db, ctx, ExchangeRecordKind.Vehicle, source.SourceId, local, local, local.UpdatedAt, VehicleFields, SourceOf(source),
                "транспорт объекта «" + await PersonNameAsync(db, local.PersonId, cancellationToken) + "»", merged: false, authorUserId: null,
                check: null, cancellationToken);
    }

    /// <summary>Обновляет принятую прежде сводку: изменённое содержимое — новой редакцией получателя.</summary>
    public static async Task<ExchangeUpdateResult> UpdateReportAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeReport source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var state = await ReportStateAsync(db, localId, cancellationToken);
        if (state is null)
        {
            return ExchangeUpdateResult.None;
        }

        var report = state.Report;
        await RememberPersonsAsync(db, ctx, report.CaseId, cancellationToken);
        var number = await CaseNumberAsync(db, report.CaseId, cancellationToken);
        var label = report.Kind.Label() + " от " + report.ReportDate.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) + " (дело № " + number + ")";
        return await UpdateAsync(
            db, ctx, ExchangeRecordKind.Report, source.SourceId, state, report, report.UpdatedAt, ReportFields, SourceOf(source, ctx),
            label, merged: false, ctx.User(source.RevisionAuthorUserSourceId), check: null, cancellationToken);
    }

    /// <summary>Обновляет принятое прежде событие связи.</summary>
    public static async Task<ExchangeUpdateResult> UpdateEventAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeLinkEvent source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.LinkEvents.FirstOrDefaultAsync(e => e.Id == localId, cancellationToken);
        return local is null
            ? ExchangeUpdateResult.None
            : await UpdateAsync(
                db, ctx, ExchangeRecordKind.LinkEvent, source.SourceId, local, local, local.UpdatedAt, EventFields, SourceOf(source),
                "событие связи «" + local.Title + "»", merged: false, authorUserId: null, check: null, cancellationToken);
    }

    /// <summary>Обновляет принятого прежде участника события связи.</summary>
    public static async Task<ExchangeUpdateResult> UpdateParticipantAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeParticipant source, int localId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(source);
        await FlushAsync(db, cancellationToken);
        var local = await db.LinkEventParticipants.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken);
        if (local is null)
        {
            return ExchangeUpdateResult.None;
        }

        var title = await db.LinkEvents.AsNoTracking().Where(e => e.Id == local.EventId).Select(e => e.Title).FirstOrDefaultAsync(cancellationToken);
        return await UpdateAsync(
            db, ctx, ExchangeRecordKind.Participant, source.SourceId, local, local, local.UpdatedAt, ParticipantFields, SourceOf(source),
            "участник «" + await PersonNameAsync(db, local.PersonId, cancellationToken) + "» события «" + title + "»", merged: false,
            authorUserId: null, check: null, cancellationToken);
    }

    // ---------- решение Руководителя по конфликту изменений ----------

    /// <summary>
    /// Применяет решение по конфликту изменений СРАЗУ (ADR-0068 v0.4): «принять версию источника» записывает значения
    /// источника из строки конфликта, «оставить свою» запись не меняет — снимок принимает значения источника, и тот же
    /// конфликт не вернётся. Строка конфликта, изменённая вызывающим в том же контексте, сохраняется тем же сохранением.
    /// <see langword="null"/> — записано; иначе — почему нельзя (ничего не сохранено).
    /// </summary>
    /// <param name="db">Контекст — тот, в котором отслеживается строка конфликта.</param>
    /// <param name="conflict">Конфликт изменений.</param>
    /// <param name="acceptSource">«Принять версию источника»; иначе — «оставить свою».</param>
    /// <param name="note">Пометка новой редакции сводки.</param>
    /// <param name="authorUserId">Кто решил — автор новой редакции сводки.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<string?> ApplyDecisionAsync(
        InvestigationDbContext db, ExchangeImportConflict conflict, bool acceptSource, string note, int? authorUserId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(conflict);
        var changes = ParseChanges(conflict.Changes);
        if (conflict.LocalId is not { } localId || changes.Count == 0)
        {
            return "В конфликте нет записи или полей — решать нечего.";
        }

        var row = await db.ExchangeSourceRecords.FirstOrDefaultAsync(
            r => r.SourceInstance == conflict.SourceInstance && r.Kind == conflict.RecordKind && r.SourceId == conflict.SourceId,
            cancellationToken);
        if (row is null)
        {
            return "Запись источника не сопоставлена — решение не применить.";
        }

        if (acceptSource)
        {
            var apply = new ExchangeApply(db, note, authorUserId);
            var problem = conflict.RecordKind switch
            {
                ExchangeRecordKind.Case => await AcceptCaseAsync(db, localId, changes, apply, cancellationToken),
                ExchangeRecordKind.Person => await AcceptPersonAsync(db, localId, changes, apply, cancellationToken),
                ExchangeRecordKind.Address => Accept(await db.PersonAddresses.FirstOrDefaultAsync(a => a.Id == localId, cancellationToken), AddressFields, changes, apply),
                ExchangeRecordKind.Phone => Accept(await db.PersonPhones.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken), PhoneFields, changes, apply),
                ExchangeRecordKind.Vehicle => Accept(await db.PersonVehicles.FirstOrDefaultAsync(v => v.Id == localId, cancellationToken), VehicleFields, changes, apply),
                ExchangeRecordKind.Report => Accept(await ReportStateAsync(db, localId, cancellationToken), ReportFields, changes, apply),
                ExchangeRecordKind.LinkEvent => Accept(await db.LinkEvents.FirstOrDefaultAsync(e => e.Id == localId, cancellationToken), EventFields, changes, apply),
                ExchangeRecordKind.Participant => Accept(await db.LinkEventParticipants.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken), ParticipantFields, changes, apply),
                _ => "Этот вид записи изменениями не переносится.",
            };
            if (problem is not null)
            {
                return problem;
            }
        }

        // Снимок принимает значения источника: следующее изменение источника сравнится уже с ними.
        var snapshot = ParseSnapshot(row.Snapshot) ?? new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var change in changes)
        {
            snapshot[change.Key] = change.Source;
        }

        row.Snapshot = Serialize(snapshot);
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception)
        {
            db.ChangeTracker.Clear();
            return "Записать значения источника нельзя — нарушилось бы правило записи (" + (exception.InnerException?.Message ?? exception.Message) + ").";
        }

        return null;
    }

    /// <summary>Поля конфликта изменений из строки конфликта (битое — пусто).</summary>
    public static IReadOnlyList<ExchangeFieldChange> ParseChanges(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<ExchangeFieldChange>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private static async Task<string?> AcceptCaseAsync(
        InvestigationDbContext db, int localId, IReadOnlyList<ExchangeFieldChange> changes, ExchangeApply apply, CancellationToken cancellationToken)
    {
        var local = await db.Cases.FirstOrDefaultAsync(c => c.Id == localId, cancellationToken);
        if (local is null)
        {
            return "Своего дела больше нет — принимать нечего.";
        }

        if (changes.FirstOrDefault(c => c.Key == "number")?.Source is { } number
            && await db.Cases.AnyAsync(c => c.Id != localId && c.DivisionId == local.DivisionId && c.Number == number, cancellationToken))
        {
            return "Номер «" + number + "» в подразделении уже занят другим делом — принять номер источника нельзя.";
        }

        return Accept(local, CaseFields, changes, apply);
    }

    private static async Task<string?> AcceptPersonAsync(
        InvestigationDbContext db, int localId, IReadOnlyList<ExchangeFieldChange> changes, ExchangeApply apply, CancellationToken cancellationToken)
    {
        var local = await db.Persons.FirstOrDefaultAsync(p => p.Id == localId, cancellationToken);
        var problem = Accept(local, PersonFields, changes, apply);
        if (problem is null)
        {
            await EnsureUnidentifiedNumberAsync(db, local!, cancellationToken);
        }

        return problem;
    }

    private static string? Accept<T>(T? local, IReadOnlyList<UpdateField<T>> fields, IReadOnlyList<ExchangeFieldChange> changes, ExchangeApply apply)
        where T : class
    {
        if (local is null)
        {
            return "Своей записи больше нет — принимать нечего.";
        }

        // Поля — в порядке описания (вид сводки раньше содержимого: текст для поиска считается по виду).
        foreach (var field in fields)
        {
            if (changes.FirstOrDefault(c => c.Key == field.Key) is { } change)
            {
                field.Set(local, change.Source, apply);
            }
        }

        return null;
    }

    // ---------- общее ----------

    private static async Task<ExchangeUpdateResult> UpdateAsync<T>(
        InvestigationDbContext db,
        ExchangeUpdateContext ctx,
        ExchangeRecordKind kind,
        int sourceId,
        T local,
        IClassified record,
        DateTime? localUpdatedAt,
        IReadOnlyList<UpdateField<T>> fields,
        Dictionary<string, string?> source,
        string label,
        bool merged,
        int? authorUserId,
        Func<MergeResult<T>, CancellationToken, Task>? check,
        CancellationToken cancellationToken)
        where T : class
    {
        var row = await db.ExchangeSourceRecords.AsNoTracking()
            .Where(r => r.SourceInstance == ctx.Source && r.Kind == kind && r.SourceId == sourceId)
            .Select(r => new { r.Id, r.Snapshot, r.ImportId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return ExchangeUpdateResult.None;
        }

        // Снимок: свой (v0.4). У принятых до v0.4: объединённое дело — значения источника молча (своё дело источником не
        // было, сравнивать не с чем); запись, не менявшаяся после того приёма, — её нынешние значения (это и есть принятое);
        // иначе снимок неизвестен — отличия идут конфликтом.
        var snapshot = ParseSnapshot(row.Snapshot);
        if (snapshot is null && merged)
        {
            await SaveSnapshotAsync(db, row.Id, source, cancellationToken);
            return ExchangeUpdateResult.None;
        }

        if (snapshot is null
            && ctx.ImportFinished.TryGetValue(row.ImportId, out var finished) && finished is { } end
            && (localUpdatedAt is null || localUpdatedAt <= end))
        {
            snapshot = fields.ToDictionary(f => f.Key, f => f.Get(local), StringComparer.Ordinal);
        }

        var result = Merge(local, fields, source, snapshot, new ExchangeApply(db, ctx.Note, authorUserId));
        if (check is not null)
        {
            await check(result, cancellationToken);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Записанное нарушило бы правило таблицы: внутри транзакции EF откатился к своей точке сохранения — не пишем
            // ничего, все изменённые у источника поля идут конфликтом (решит Руководитель).
            db.ChangeTracker.Clear();
            result.ToConflicts();
        }

        await SaveSnapshotAsync(db, row.Id, result.Next, cancellationToken);
        var appliedLabels = result.Applied.Select(a => fields.First(f => f.Key == a.Key).Label).ToList();
        if (result.Conflicts.Count > 0)
        {
            await UpsertUpdateConflictAsync(db, ctx, kind, sourceId, label, result, fields, record, cancellationToken);
            return new ExchangeUpdateResult(
                ExchangeUpdateStatus.Conflict, label, [.. result.Applied.Select(a => a.Key)], [.. result.Conflicts.Select(c => c.Key)],
                label + ": конфликт — " + string.Join(", ", result.Conflicts.Select(c => fields.First(f => f.Key == c.Key).Label))
                + (appliedLabels.Count > 0 ? "; изменено — " + string.Join(", ", appliedLabels) : string.Empty));
        }

        await ClearUpdateConflictAsync(db, ctx, kind, sourceId, cancellationToken);
        return result.Applied.Count > 0
            ? new ExchangeUpdateResult(ExchangeUpdateStatus.Updated, label, [.. result.Applied.Select(a => a.Key)], [],
                label + ": изменено — " + string.Join(", ", appliedLabels))
            : ExchangeUpdateResult.None with { Label = label };
    }

    private static MergeResult<T> Merge<T>(
        T local, IReadOnlyList<UpdateField<T>> fields, Dictionary<string, string?> source, Dictionary<string, string?>? snapshot, ExchangeApply apply)
    {
        var result = new MergeResult<T>(fields, apply);
        foreach (var field in fields)
        {
            var src = source.GetValueOrDefault(field.Key);
            var loc = field.Get(local);
            if (field.RaiseOnly)
            {
                // Гриф — только вверх: поднятый у источника поднимается без конфликта, понижение не переносится (ТБ-070).
                if (ExchangeCanon.ToInt(src) is { } up && (ExchangeCanon.ToInt(loc) ?? 0) < up)
                {
                    field.Set(local, src, apply);
                    result.Applied.Add((field.Key, loc, src, loc));
                }

                result.Next[field.Key] = src;
                continue;
            }

            if (snapshot is null || !snapshot.TryGetValue(field.Key, out var baseValue))
            {
                // Снимок неизвестен: совпало — запоминаем; разошлось — чьё это изменение, не узнать: конфликт.
                if (string.Equals(src, loc, StringComparison.Ordinal))
                {
                    result.Next[field.Key] = src;
                }
                else
                {
                    result.Conflicts.Add((field.Key, null, false, src, loc));
                }

                continue;
            }

            switch (ExchangeThreeWay.Decide(baseValue, src, loc))
            {
                case ExchangeThreeWay.Action.Apply:
                    field.Set(local, src, apply);
                    result.Applied.Add((field.Key, baseValue, src, loc));
                    result.Next[field.Key] = src;
                    break;
                case ExchangeThreeWay.Action.Converged:
                    result.Next[field.Key] = src;
                    break;
                case ExchangeThreeWay.Action.Conflict:
                    result.Conflicts.Add((field.Key, baseValue, true, src, loc));
                    result.Next[field.Key] = baseValue;
                    break;
                default:
                    result.Next[field.Key] = baseValue;
                    break;
            }
        }

        return result;
    }

    // Сохранить то, что вызывающий ещё не сохранил: сбой записи этой записи откатит отслеживание (Clear) и не должен
    // утянуть с собой чужие изменения — заведённые записи и строки карты.
    private static async Task FlushAsync(InvestigationDbContext db, CancellationToken cancellationToken)
    {
        if (db.ChangeTracker.HasChanges())
        {
            await db.SaveChangesAsync(cancellationToken);
        }
    }

    private static Task<int> SaveSnapshotAsync(InvestigationDbContext db, long rowId, Dictionary<string, string?> next, CancellationToken cancellationToken)
    {
        var json = Serialize(next);
        return db.ExchangeSourceRecords.Where(r => r.Id == rowId).ExecuteUpdateAsync(s => s.SetProperty(r => r.Snapshot, json), cancellationToken);
    }

    private static async Task UpsertUpdateConflictAsync<T>(
        InvestigationDbContext db,
        ExchangeUpdateContext ctx,
        ExchangeRecordKind kind,
        int sourceId,
        string label,
        MergeResult<T> result,
        IReadOnlyList<UpdateField<T>> fields,
        IClassified record,
        CancellationToken cancellationToken)
    {
        var changes = result.Conflicts.Select(c =>
        {
            var field = fields.First(f => f.Key == c.Key);
            return new ExchangeFieldChange(
                c.Key,
                field.Label,
                c.Base,
                c.Source,
                c.Local,
                c.BaseKnown ? field.Display(c.Base, ctx.Names) : "неизвестно",
                field.Display(c.Source, ctx.Names),
                field.Display(c.Local, ctx.Names),
                c.BaseKnown);
        }).ToList();
        var details = string.Join(
            "; ",
            changes.Select(c => c.Label + ": было " + (c.BaseText ?? "—") + ", у источника " + (c.SourceText ?? "—") + ", у вас " + (c.LocalText ?? "—")));
        var localId = await db.ExchangeSourceRecords.AsNoTracking()
            .Where(r => r.SourceInstance == ctx.Source && r.Kind == kind && r.SourceId == sourceId)
            .Select(r => (int?)r.LocalId)
            .FirstOrDefaultAsync(cancellationToken);
        var conflict = await db.ExchangeImportConflicts.FirstOrDefaultAsync(
            c => c.SourceInstance == ctx.Source && c.Kind == ExchangeConflictKind.RecordUpdate && c.RecordKind == kind && c.SourceId == sourceId,
            cancellationToken);
        if (conflict is null)
        {
            conflict = new ExchangeImportConflict
            {
                SourceInstance = ctx.Source,
                Kind = ExchangeConflictKind.RecordUpdate,
                RecordKind = kind,
                SourceId = sourceId,
                Reason = string.Empty,
                CreatedAt = ctx.Now,
            };
            db.ExchangeImportConflicts.Add(conflict);
        }
        else if (conflict.Status != ExchangeConflictStatus.Pending)
        {
            // Конфликт той же записи после прежнего решения — новый: «найден» — теперь, прежнее решение к нему не относится
            // (оно в журнале аудита).
            conflict.CreatedAt = ctx.Now;
        }

        conflict.ImportId = ctx.ImportId;
        conflict.Status = ExchangeConflictStatus.Pending;
        conflict.Decision = null;
        conflict.DecisionLocalId = null;
        conflict.DecisionNumber = null;
        conflict.DecisionComment = null;
        conflict.DecidedByUserId = null;
        conflict.DecidedAt = null;
        conflict.AppliedAt = null;
        conflict.AppliedImportId = null;
        conflict.LocalId = localId;
        conflict.Reason = Clip("изменено и у источника, и у вас: " + string.Join(", ", changes.Select(c => c.Label)), 500);
        conflict.Label = Clip(label, 500);
        conflict.Details = Clip(details, ExchangeRecordImporter.DetailsLimit);
        conflict.Changes = Serialize(changes);

        // Гриф и подразделение — своей записи: конфликт видит тот, кому доступна запись (ТБ-020/021).
        conflict.Classification = record.Classification;
        conflict.DivisionId = record.DivisionId;
        conflict.UpdatedAt = ctx.Now;
        await db.SaveChangesAsync(cancellationToken);
    }

    // Конфликт изменений, который этот приём больше не подтверждает (сошлось у обоих или источник вернул прежнее), снимается.
    private static Task<int> ClearUpdateConflictAsync(
        InvestigationDbContext db, ExchangeUpdateContext ctx, ExchangeRecordKind kind, int sourceId, CancellationToken cancellationToken) =>
        db.ExchangeImportConflicts
            .Where(c => c.SourceInstance == ctx.Source && c.Kind == ExchangeConflictKind.RecordUpdate && c.RecordKind == kind
                        && c.SourceId == sourceId && c.Status == ExchangeConflictStatus.Pending)
            .ExecuteUpdateAsync(s => s
                .SetProperty(c => c.Status, ExchangeConflictStatus.Cleared)
                .SetProperty(c => c.UpdatedAt, ctx.Now), cancellationToken);

    // Стал неустановленным без номера — номер выдаётся, как при ручной правке (следующий в деле).
    private static async Task EnsureUnidentifiedNumberAsync(InvestigationDbContext db, Person person, CancellationToken cancellationToken)
    {
        if (!person.IsUnidentified || person.UnidentifiedNumber is not null)
        {
            return;
        }

        var max = await db.Persons.AsNoTracking()
            .Where(p => p.CaseId == person.CaseId && p.UnidentifiedNumber != null)
            .MaxAsync(p => p.UnidentifiedNumber, cancellationToken);
        person.UnidentifiedNumber = (max ?? 0) + 1;
    }

    private static async Task<ExchangeReportState?> ReportStateAsync(InvestigationDbContext db, int reportId, CancellationToken cancellationToken)
    {
        var report = await db.CaseReports.FirstOrDefaultAsync(r => r.Id == reportId, cancellationToken);
        if (report is null)
        {
            return null;
        }

        var content = await db.CaseReportRevisions.AsNoTracking()
            .Where(v => v.ReportId == report.Id && v.Number == report.CurrentRevision)
            .Select(v => v.ContentJson)
            .FirstOrDefaultAsync(cancellationToken);
        return new ExchangeReportState(report, content);
    }

    private static async Task RememberPersonsAsync(InvestigationDbContext db, ExchangeUpdateContext ctx, int caseId, CancellationToken cancellationToken)
    {
        var persons = await db.Persons.AsNoTracking().Where(p => p.CaseId == caseId).Select(p => new { p.Id, p.DisplayName }).ToListAsync(cancellationToken);
        foreach (var person in persons)
        {
            ctx.Names.Persons[person.Id] = person.DisplayName;
        }
    }

    private static Task<string?> CaseNumberAsync(InvestigationDbContext db, int caseId, CancellationToken cancellationToken) =>
        db.Cases.AsNoTracking().Where(c => c.Id == caseId).Select(c => (string?)c.Number).FirstOrDefaultAsync(cancellationToken);

    private static Task<string?> PersonNameAsync(InvestigationDbContext db, int personId, CancellationToken cancellationToken) =>
        db.Persons.AsNoTracking().Where(p => p.Id == personId).Select(p => (string?)p.DisplayName).FirstOrDefaultAsync(cancellationToken);

    private static string Serialize<TValue>(TValue value) => JsonSerializer.Serialize(value, Json);

    private static Dictionary<string, string?>? ParseSnapshot(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string?>>(json, Json);
            return parsed is null ? null : new Dictionary<string, string?>(parsed, StringComparer.Ordinal);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Plain(string? value, ExchangeNames names) => value;

    private static string? DateText(string? value, ExchangeNames names) =>
        ExchangeCanon.ToDate(value) is { } date ? date.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : null;

    private static string? MomentText(string? value, ExchangeNames names) =>
        ExchangeCanon.ToMoment(value) is { } moment ? moment.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture) + " UTC" : null;

    private static string? BoolText(string? value, ExchangeNames names) => value is null ? null : value == "1" ? "да" : "нет";

    private static string? ClassificationText(string? value, ExchangeNames names) =>
        ExchangeCanon.ToShort(value) is { } level ? ClassificationLevels.Label(level) : null;

    // Содержимое сводки словами — коротко: Руководителю видно, что разошлось; целиком — в карточке сводки.
    private static string ReportText(string json)
    {
        var content = CaseReportContent.FromJson(json);
        var parts = new List<string?>();
        foreach (var e in content.EventRows)
        {
            parts.Add(string.Join(" ", new[] { e.Time, e.Place, e.Description, e.Persons, e.Vehicles }.Where(s => !string.IsNullOrWhiteSpace(s))));
        }

        parts.AddRange([content.Identity, content.Addresses, content.Occupation, content.Family, content.Characterizing, content.Conclusion]);
        var text = string.Join("; ", parts.Where(s => !string.IsNullOrWhiteSpace(s)));
        return text.Length == 0 ? "(пусто)" : Clip(text, 300);
    }

    private static string Name(Dictionary<int, string> names, string? id) =>
        ExchangeCanon.ToInt(id) is { } key
            ? names.TryGetValue(key, out var name) ? name : "№ " + key.ToString(CultureInfo.InvariantCulture)
            : "—";

    private static string Clip(string text, int limit) => text.Length <= limit ? text : text[..(limit - 1)] + "…";

    /// <summary>Поле записи: как прочесть и записать значение в виде получателя и как показать его человеку.</summary>
    private sealed record UpdateField<T>(
        string Key,
        string Label,
        Func<T, string?> Get,
        Action<T, string?, ExchangeApply> Set,
        Func<string?, ExchangeNames, string?> Display,
        bool RaiseOnly = false);

    /// <summary>Итог сравнения записи: записанные поля, конфликтные и следующий снимок.</summary>
    private sealed class MergeResult<T>(IReadOnlyList<UpdateField<T>> fields, ExchangeApply apply)
    {
        public List<(string Key, string? Base, string? Source, string? Local)> Applied { get; } = [];

        public List<(string Key, string? Base, bool BaseKnown, string? Source, string? Local)> Conflicts { get; } = [];

        public Dictionary<string, string?> Next { get; } = new(StringComparer.Ordinal);

        public bool WasApplied(string key) => Applied.Exists(a => a.Key == key);

        // Записанное поле вернуть к своему значению и сделать конфликтом (занятый номер дела).
        public void Revert(T local, string key)
        {
            var applied = Applied.Find(a => a.Key == key);
            fields.First(f => f.Key == key).Set(local, applied.Local, apply);
            Applied.Remove(applied);
            Conflicts.Add((key, applied.Base, true, applied.Source, applied.Local));
            Next[key] = applied.Base;
        }

        // Записать ничего нельзя (сбой правила таблицы): все записанные поля — конфликтом, снимок по ним прежний.
        public void ToConflicts()
        {
            foreach (var applied in Applied)
            {
                Conflicts.Add((applied.Key, applied.Base, true, applied.Source, applied.Local));
                Next[applied.Key] = applied.Base;
            }

            Applied.Clear();
        }
    }
}

/// <summary>Значения полей в виде получателя: строки без крайних пробелов, числа и даты — инвариантно (ADR-0068 v0.4).</summary>
internal static class ExchangeCanon
{
    private const string MomentFormat = "yyyy-MM-dd'T'HH:mm:ss.ffffff";

    public static string? Text(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    public static string? Int(int? value) => value?.ToString(CultureInfo.InvariantCulture);

    public static string Int(short value) => value.ToString(CultureInfo.InvariantCulture);

    public static string Enum<TEnum>(TEnum value)
        where TEnum : struct, System.Enum =>
        Convert.ToInt32(value, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture);

    public static string? Date(DateOnly? value) => value?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // Время — с точностью базы (микросекунды) и как UTC: прочитанное из базы и пришедшее в пакете сравниваются одинаково.
    public static string? Moment(DateTime? value) =>
        value is { } moment
            ? new DateTime(moment.Ticks - (moment.Ticks % 10), DateTimeKind.Utc).ToString(MomentFormat, CultureInfo.InvariantCulture)
            : null;

    public static string Bool(bool value) => value ? "1" : "0";

    // Содержимое сводки — тем же JSON, что пишет ввод (jsonb базы меняет порядок ключей и пробелы).
    public static string? Report(string? json) => json is null ? null : CaseReportContent.FromJson(json).ToJson();

    public static string Compose(params string?[] parts) => JsonSerializer.Serialize(parts);

    public static string?[] Split(string? value, int count)
    {
        string?[]? parts = null;
        if (!string.IsNullOrEmpty(value))
        {
            try
            {
                parts = JsonSerializer.Deserialize<string?[]>(value);
            }
            catch (JsonException)
            {
                parts = null;
            }
        }

        var result = new string?[count];
        if (parts is not null)
        {
            Array.Copy(parts, result, Math.Min(parts.Length, count));
        }

        return result;
    }

    public static int? ToInt(string? value) =>
        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static short? ToShort(string? value) =>
        short.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number) ? number : null;

    public static DateOnly? ToDate(string? value) =>
        DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;

    public static DateTime? ToMoment(string? value) =>
        DateTime.TryParseExact(value, MomentFormat, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var moment)
            ? DateTime.SpecifyKind(moment, DateTimeKind.Utc)
            : null;
}
