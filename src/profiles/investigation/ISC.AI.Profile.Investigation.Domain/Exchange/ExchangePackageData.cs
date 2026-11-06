using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Exchange;

/// <summary>
/// Содержимое пакета обмена между офисами (<c>data.json.enc</c>, ТФ-ОБМ-01, ТО-инф-14, ADR-0068), версия формата 1. Каждая
/// запись несёт свой номер в экземпляре-источнике (<c>SourceId</c>): по нему импорт сопоставит её при следующем пакете
/// (идемпотентность). Ссылки на справочники, подразделения и сотрудников — номерами источника, а сами они перечислены
/// естественными ключами (вид и название, код, имя входа): номера у офисов разные.
/// </summary>
/// <remarks>
/// Шаблонов и векторов лиц здесь нет и быть не может (ТБ-076): лицо описано только кадром, таймкодом и рамкой. Перечисления
/// записываются именами (устойчивее номеров при изменении состава); виды носителей и этапы верификации — строками имён из
/// пакета «Медиа» (<c>MediaKind</c>, <c>VerificationStage</c>, <c>VerificationVerdict</c>, <c>ConfirmationMode</c>).
/// </remarks>
/// <param name="FormatVersion">Версия формата содержимого.</param>
/// <param name="SourceInstance">Номер экземпляра-источника.</param>
/// <param name="Year">Год периода.</param>
/// <param name="Month">Месяц периода.</param>
/// <param name="CreatedAt">Когда сформирован.</param>
/// <param name="Divisions">Подразделения, на которые ссылаются записи.</param>
/// <param name="References">Записи справочников, на которые ссылаются записи.</param>
/// <param name="Users">Сотрудники, на которых ссылаются записи (авторы, эксперты, верификаторы).</param>
/// <param name="Cases">Дела.</param>
/// <param name="Persons">Объекты дел с реквизитами.</param>
/// <param name="ReferencePhotos">Эталоны объектов.</param>
/// <param name="LinkEvents">События связи с участниками.</param>
/// <param name="Reports">Сводки и справки — действующая редакция.</param>
/// <param name="Assets">Носители: реквизиты, привязки к делам и файл пакета (если файл в этом пакете).</param>
/// <param name="Faces">Лица, на которые ссылаются эталоны и появления.</param>
/// <param name="Appearances">Появления с решениями верификации.</param>
public sealed record ExchangePackageData(
    int FormatVersion,
    string SourceInstance,
    int Year,
    int Month,
    DateTimeOffset CreatedAt,
    IReadOnlyList<ExchangeDivision> Divisions,
    IReadOnlyList<ExchangeReference> References,
    IReadOnlyList<ExchangeUser> Users,
    IReadOnlyList<ExchangeCase> Cases,
    IReadOnlyList<ExchangePerson> Persons,
    IReadOnlyList<ExchangeReferencePhoto> ReferencePhotos,
    IReadOnlyList<ExchangeLinkEvent> LinkEvents,
    IReadOnlyList<ExchangeReport> Reports,
    IReadOnlyList<ExchangeAsset> Assets,
    IReadOnlyList<ExchangeFace> Faces,
    IReadOnlyList<ExchangeAppearance> Appearances)
{
    /// <summary>Текущая версия формата содержимого.</summary>
    public const int CurrentFormat = 1;

    /// <summary>Вид пакета в манифесте.</summary>
    public const string Kind = "isc-exchange";

    /// <summary>Имя файла содержимого в пакете.</summary>
    public const string FileName = "data.json.enc";

    /// <summary>Каталог файлов носителей в пакете.</summary>
    public const string MediaFolder = "media";

    /// <summary>Настройки JSON содержимого: перечисления — именами, кириллица — как есть.</summary>
    public static JsonSerializerOptions Json { get; } = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>Счётчики для манифеста и сверки при импорте (ЦС-08): дела, объекты, связи, сводки, носители, появления.</summary>
    public IReadOnlyDictionary<string, int> Counts() => new Dictionary<string, int>(StringComparer.Ordinal)
    {
        ["cases"] = Cases.Count,
        ["persons"] = Persons.Count,
        ["links"] = Persons.Count(p => p.LinkedToPersonSourceId is not null) + LinkEvents.Count,
        ["reports"] = Reports.Count,
        ["assets"] = Assets.Count,
        ["files"] = Assets.Count(a => a.File is not null),
        ["appearances"] = Appearances.Count,
    };
}

/// <summary>Подразделение: по коду, а без кода — по названию; направление (ОН/ОУ/ОТМ) — как в источнике.</summary>
public sealed record ExchangeDivision(int SourceId, string Name, string? Code, CaseDirection? Direction, int? ParentSourceId);

/// <summary>Запись справочника: вид и название — естественный ключ.</summary>
public sealed record ExchangeReference(int SourceId, ReferenceKind Kind, string Name, string? Code);

/// <summary>Сотрудник: имя входа и ФИО — номера учётных записей у офисов разные.</summary>
public sealed record ExchangeUser(int SourceId, string UserName, string? DisplayName);

/// <summary>Дело.</summary>
public sealed record ExchangeCase(
    int SourceId,
    string Number,
    string Title,
    CaseKind Kind,
    DateOnly OpenedAt,
    CaseStatus Status,
    DateTime? ClosedAt,
    string? Basis,
    int DivisionSourceId,
    short Classification,
    int? InvestigatorUserSourceId,
    int? CreatedByUserSourceId,
    ExchangeTask? Task,
    DateTime CreatedAt,
    DateTime? UpdatedAt);

/// <summary>Реквизиты задания по объекту (дело вида «задание»): ГУ и номер задания — ключ конфликта при импорте.</summary>
public sealed record ExchangeTask(
    string? TaskNumber,
    int? InitiatorUnitSourceId,
    string? InitiatorName,
    int? InitiatorRankSourceId,
    int? InitiatorPositionSourceId,
    string? InitiatorPhone,
    string? InitiatorDetails,
    string? Justification,
    string? Purpose,
    string? TaskNotes);

/// <summary>Объект дела с анкетой и реквизитами.</summary>
public sealed record ExchangePerson(
    int SourceId,
    int CaseSourceId,
    string DisplayName,
    bool IsUnidentified,
    int? UnidentifiedNumber,
    PersonRole Role,
    string? RoleInCase,
    DateOnly? BirthDate,
    int? BirthYear,
    string? BirthPlace,
    string? WorkPlace,
    string? Residence,
    PersonSex? Sex,
    string? Alias,
    string? Citizenship,
    string? Orientation,
    string? Notes,
    int? LinkedToPersonSourceId,
    int? LinkTypeSourceId,
    short Classification,
    int DivisionSourceId,
    IReadOnlyList<ExchangeAddress> Addresses,
    IReadOnlyList<ExchangePhone> Phones,
    IReadOnlyList<ExchangeVehicle> Vehicles);

/// <summary>Адрес объекта.</summary>
public sealed record ExchangeAddress(int SourceId, AddressKind Kind, string Text, string? Notes, short Classification);

/// <summary>Телефон объекта.</summary>
public sealed record ExchangePhone(int SourceId, PhoneKind Kind, string Number, string? Notes, short Classification);

/// <summary>Транспорт объекта.</summary>
public sealed record ExchangeVehicle(
    int SourceId, string? PlateNumber, string? Make, string? Model, string? Color, string? Notes, short Classification);

/// <summary>Эталон объекта: носитель и лицо на нём.</summary>
public sealed record ExchangeReferencePhoto(
    int SourceId,
    int PersonSourceId,
    int AssetSourceId,
    int? FaceSourceId,
    float? QualityScore,
    string? Source,
    string? LegalBasis,
    DateOnly? ReviewDueAt,
    int? AddedByUserSourceId,
    int? SupersededBySourceId,
    short Classification);

/// <summary>Событие связи с участниками.</summary>
public sealed record ExchangeLinkEvent(
    int SourceId,
    LinkEventKind Kind,
    string Title,
    DateOnly? OccurredOn,
    string? Description,
    int? CreatedByUserSourceId,
    short Classification,
    int DivisionSourceId,
    IReadOnlyList<ExchangeParticipant> Participants);

/// <summary>Участник события связи.</summary>
public sealed record ExchangeParticipant(
    int SourceId,
    int PersonSourceId,
    string? Role,
    LinkLevel Level,
    bool InSearch,
    string? VehiclePlate,
    string? VehicleDetails,
    string? ContactAddress,
    short Classification);

/// <summary>Сводка или справка — действующая редакция.</summary>
public sealed record ExchangeReport(
    int SourceId,
    int CaseSourceId,
    int? PersonSourceId,
    CaseReportKind Kind,
    DateOnly ReportDate,
    bool IsActive,
    int? CreatedByUserSourceId,
    int Revision,
    string ContentJson,
    int? RevisionAuthorUserSourceId,
    DateTime RevisionAt,
    short Classification,
    int DivisionSourceId);

/// <summary>
/// Носитель: реквизиты и привязки к делам. <see cref="File"/> — файл в этом пакете (путь через «/»), <see langword="null"/> —
/// носитель ушёл прежним пакетом, здесь только реквизиты (на него ссылаются эталоны или появления этого пакета).
/// </summary>
public sealed record ExchangeAsset(
    int SourceId,
    string Kind,
    string OriginalFileName,
    string ContentType,
    string ContentHash,
    long ByteSize,
    long? DurationMs,
    string? Source,
    DateTimeOffset? CapturedAt,
    DateTimeOffset? RecordedAt,
    double? FrameRate,
    int? FrameWidth,
    int? FrameHeight,
    int? UploadedByUserSourceId,
    int? SourceAssetSourceId,
    long? SourceTimestampMs,
    string? EditOperations,
    short Classification,
    int DivisionSourceId,
    DateTime UploadedAt,
    string? File,
    IReadOnlyList<ExchangeCaseMediaLink> Cases);

/// <summary>Привязка носителя к делу: место — реквизит привязки.</summary>
public sealed record ExchangeCaseMediaLink(int CaseSourceId, string? Place, int? LinkedByUserSourceId, DateTime LinkedAt);

/// <summary>Лицо: кадр, таймкод и рамка — по ним импорт найдёт то же лицо в своей индексации (ТФ-ОБМ-02).</summary>
public sealed record ExchangeFace(
    int SourceId, int AssetSourceId, int FrameIndex, long TimestampMs, float BoxX, float BoxY, float BoxWidth, float BoxHeight);

/// <summary>Появление объекта на носителе с решениями верификации (провенанс, ТФ-ОБМ-01).</summary>
public sealed record ExchangeAppearance(
    int SourceId,
    int PersonSourceId,
    int CaseSourceId,
    int AssetSourceId,
    int FaceSourceId,
    int? FrameIndex,
    long? FrameTimestampMs,
    double Similarity,
    AppearanceStatus Status,
    DateTime ConfirmedAtUtc,
    int ExpertUserSourceId,
    int? VerifierUserSourceId,
    DateTime? RevokedAtUtc,
    int? RevokedByUserSourceId,
    string? RevokeReason,
    short Classification,
    IReadOnlyList<ExchangeDecision> Decisions);

/// <summary>Решение верификации: этап, вердикт, обоснование, кто и когда.</summary>
public sealed record ExchangeDecision(
    string Stage, string Verdict, string Rationale, DateTime DecidedAtUtc, int UserSourceId, string ConfirmationMode);
