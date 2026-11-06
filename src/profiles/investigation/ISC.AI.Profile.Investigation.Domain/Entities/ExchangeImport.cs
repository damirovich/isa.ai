using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>Состояние приёма пакета обмена.</summary>
public enum ExchangeImportStatus : short
{
    /// <summary>Пакет принимается.</summary>
    Running = 1,

    /// <summary>Пакет принят (конфликты и отложенное — в счётчиках).</summary>
    Succeeded = 2,

    /// <summary>Приём не удался — причина в строке; принятое до сбоя остаётся в карте, повторный приём его доводит.</summary>
    Failed = 3,

    /// <summary>Прерван перезапуском хоста; повторный приём доводит его по карте.</summary>
    Interrupted = 4,
}

/// <summary>
/// Приём пакета обмена из другого офиса (ТФ-ОБМ-02, ТФ-АДМ-05, ADR-0068 v0.2) — строка журнала приёмов: кто, когда, какой
/// пакет, итог и счётчики. Содержимого пакета здесь нет (ТБ-079): только метаданные.
/// </summary>
public class ExchangeImport : BaseEntity
{
    /// <summary>Когда начат (UTC).</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Когда закончен (UTC).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Состояние.</summary>
    public ExchangeImportStatus Status { get; set; }

    /// <summary>Кто запустил (Администратор).</summary>
    public int StartedByUserId { get; set; }

    /// <summary>Имя каталога пакета на носителе.</summary>
    public required string PackageName { get; set; }

    /// <summary>Экземпляр-источник (из проверенного манифеста).</summary>
    public string? SourceInstance { get; set; }

    /// <summary>Период пакета, «гггг-ММ».</summary>
    public string? Period { get; set; }

    /// <summary>SHA-256 манифеста — какой именно пакет принят.</summary>
    public string? ManifestSha256 { get; set; }

    /// <summary>Администратор подтвердил, что пакет прошёл проверку на шлюзе (ТБ-083, [Расш. 1.3]).</summary>
    public bool GatewayConfirmed { get; set; }

    /// <summary>Маркировка пакета (наибольший гриф содержимого).</summary>
    public short? Classification { get; set; }

    /// <summary>Заведено дел.</summary>
    public int CasesCreated { get; set; }

    /// <summary>Заведено объектов.</summary>
    public int PersonsCreated { get; set; }

    /// <summary>Заведено событий связи.</summary>
    public int EventsCreated { get; set; }

    /// <summary>Заведено сводок.</summary>
    public int ReportsCreated { get; set; }

    /// <summary>Принято носителей (заведено или привязано принятых раньше к новым делам — по карте).</summary>
    public int AssetsCreated { get; set; }

    /// <summary>Получено файлов носителей.</summary>
    public int FilesReceived { get; set; }

    /// <summary>Объём полученных файлов, байт (открытое содержимое).</summary>
    public long BytesReceived { get; set; }

    /// <summary>Записей, уже принятых прежде (по карте), — пропущено.</summary>
    public int AlreadyImported { get; set; }

    /// <summary>Конфликтов (дела и носители) без применённого решения — ждут очереди Руководителя.</summary>
    public int Conflicts { get; set; }

    /// <summary>Объединено со своими делами и носителями по решению Руководителя (ADR-0068 v0.3).</summary>
    public int Merged { get; set; }

    /// <summary>Дел и носителей, пропущенных по решению Руководителя (ADR-0068 v0.3).</summary>
    public int SkippedByDecision { get; set; }

    /// <summary>Эталонов и появлений, перенесённых при приёме (лицо нашлось в индексации получателя).</summary>
    public int FacesLinked { get; set; }

    /// <summary>
    /// Эталонов и появлений, ждущих сопоставления лица после приёма: носитель ещё индексируется или ждёт дело, объект или
    /// носитель (ADR-0068 v0.3). Столбец сохранил имя этапа 2 — тогда это было «отложено до этапа 3».
    /// </summary>
    public int Deferred { get; set; }

    /// <summary>Эталонов и появлений, лицо которых в индексации получателя не нашлось.</summary>
    public int FacesNotFound { get; set; }

    /// <summary>Принятых прежде записей, изменения которых перенесены (ADR-0068 v0.4).</summary>
    public int RecordsUpdated { get; set; }

    /// <summary>Конфликтов изменений — поле изменено и у источника, и у получателя (ADR-0068 v0.4).</summary>
    public int UpdateConflicts { get; set; }

    /// <summary>Носителей пакета без файла и ещё не принятых — файл ушёл прежним пакетом.</summary>
    public int MissingFiles { get; set; }

    /// <summary>Файлов, отклонённых общим приёмом «Медиа» (формат, размер, хеш оригинала).</summary>
    public int Rejected { get; set; }

    /// <summary>
    /// Сверка исходов с манифестом по видам (ЦС-08): <see langword="true"/> — сумма исходов каждого вида равна числу в
    /// манифесте; <see langword="null"/> — приём не дошёл до сверки.
    /// </summary>
    public bool? ReconciliationOk { get; set; }

    /// <summary>Сверка текстом: по каждому виду — исходы и итог (без содержимого записей).</summary>
    public string? Reconciliation { get; set; }

    /// <summary>Причина сбоя (текст для Администратора, без содержимого пакета).</summary>
    public string? Error { get; set; }
}

/// <summary>Вид записи в карте «запись источника → своя запись».</summary>
public enum ExchangeRecordKind : short
{
    /// <summary>Не задан — у конфликтов дела и носителя (вид записи нужен только конфликту изменений, ADR-0068 v0.4).</summary>
    None = 0,

    /// <summary>Дело.</summary>
    Case = 1,

    /// <summary>Объект (фигурант).</summary>
    Person = 2,

    /// <summary>Адрес объекта.</summary>
    Address = 3,

    /// <summary>Телефон объекта.</summary>
    Phone = 4,

    /// <summary>Транспорт объекта.</summary>
    Vehicle = 5,

    /// <summary>Событие связи.</summary>
    LinkEvent = 6,

    /// <summary>Участник события связи.</summary>
    Participant = 7,

    /// <summary>Сводка.</summary>
    Report = 8,

    /// <summary>Носитель.</summary>
    Asset = 9,

    /// <summary>Эталон объекта (ADR-0068 v0.3) — переносится после сопоставления лица.</summary>
    ReferencePhoto = 10,

    /// <summary>Появление объекта (ADR-0068 v0.3) — переносится после сопоставления лица, с провенансом источника.</summary>
    Appearance = 11,
}

/// <summary>
/// Карта приёма (ADR-0068 v0.2, ЦС-08): запись источника (экземпляр, вид, номер в источнике) → своя запись. По ней повторный
/// приём того же или следующего пакета не создаёт дублей, а ссылки следующих пакетов на прежние записи находят их.
/// </summary>
/// <remarks>Номер источника уникален в паре (экземпляр, вид): у одного офиса одна нумерация на таблицу.</remarks>
public class ExchangeSourceRecord
{
    /// <summary>Номер строки.</summary>
    public long Id { get; set; }

    /// <summary>Экземпляр-источник.</summary>
    public required string SourceInstance { get; set; }

    /// <summary>Вид записи.</summary>
    public ExchangeRecordKind Kind { get; set; }

    /// <summary>Номер записи в источнике.</summary>
    public int SourceId { get; set; }

    /// <summary>Номер своей записи.</summary>
    public int LocalId { get; set; }

    /// <summary>Каким приёмом сопоставлено.</summary>
    public int ImportId { get; set; }

    /// <summary>Когда (UTC).</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Снимок записи источника, с которым своя запись последний раз сведена (ADR-0068 v0.4): JSON «поле → значение» в виде
    /// получателя. По нему следующий пакет отличает изменение источника от правки получателя (трёхстороннее сравнение);
    /// <see langword="null"/> — запись принята до версии 0.4.
    /// </summary>
    public string? Snapshot { get; set; }
}

/// <summary>Вид конфликта приёма.</summary>
public enum ExchangeConflictKind : short
{
    /// <summary>У получателя уже есть дело с той же парой «ГУ + № задания» или с тем же номером в подразделении.</summary>
    Case = 1,

    /// <summary>У получателя уже есть носитель с тем же файлом (ТФ-МЕД-04), не принятый из этого источника.</summary>
    Asset = 2,

    /// <summary>
    /// Конфликт изменений (ADR-0068 v0.4): поле принятой записи изменено и у источника, и у получателя — по-разному.
    /// </summary>
    RecordUpdate = 3,
}

/// <summary>Состояние конфликта.</summary>
public enum ExchangeConflictStatus : short
{
    /// <summary>Ждёт решения Руководителя (очередь «Конфликты обмена»).</summary>
    Pending = 1,

    /// <summary>
    /// Снят: повторный приём принял запись — совпадения больше нет (своё дело переименовали, свой носитель уничтожили).
    /// </summary>
    Cleared = 2,

    /// <summary>Решён Руководителем; решение применится при следующем приёме пакета с этой записью (ADR-0068 v0.3).</summary>
    Decided = 3,

    /// <summary>Решение применено приёмом (<see cref="ExchangeImportConflict.AppliedImportId"/>).</summary>
    Applied = 4,
}

/// <summary>Решение Руководителя по конфликту приёма (ТФ-ОБМ-02, ADR-0068 v0.3).</summary>
public enum ExchangeConflictDecision : short
{
    /// <summary>Запись источника = своя запись: карта ведёт на неё, содержимое дела добавляется в своё дело.</summary>
    Merge = 1,

    /// <summary>Завести новую запись (только дело: одинаковый файл — один носитель, ТФ-МЕД-04).</summary>
    CreateNew = 2,

    /// <summary>Не переносить запись и всё, что от неё зависит, — ни сейчас, ни следующими приёмами.</summary>
    Skip = 3,

    /// <summary>Конфликт изменений: записать значения источника (сразу — они в строке конфликта, ADR-0068 v0.4).</summary>
    AcceptSource = 4,

    /// <summary>Конфликт изменений: оставить своё; снимок принимает значения источника, тот же конфликт не вернётся.</summary>
    KeepLocal = 5,
}

/// <summary>
/// Конфликт приёма (ТФ-ОБМ-02): запись пакета совпала с записью получателя — объединить, завести новую или пропустить,
/// решает Руководитель в очереди «Конфликты обмена» (не Администратор, ТБ-079). Запись и всё, что от неё зависит, до
/// применения решения не переносится. Гриф и подразделение — записи: очередь показывает конфликт только тем, кому он
/// доступен (ТБ-020/021).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ADR-0068 v0.3): решение только записывается — применяет его следующий приём пакета ключом хранителя.
/// Содержимое конфликтного дела и файлы его носителей лежат в пакете зашифрованными; расшифрованной копии до решения у
/// получателя нет. В строке — лишь описание записи (<see cref="Details"/>), чтобы Руководителю было что решать.
/// </remarks>
public class ExchangeImportConflict : BaseEntity, IClassified
{
    /// <summary>Каким приёмом обнаружен (последним).</summary>
    public int ImportId { get; set; }

    /// <summary>Экземпляр-источник.</summary>
    public required string SourceInstance { get; set; }

    /// <summary>Вид.</summary>
    public ExchangeConflictKind Kind { get; set; }

    /// <summary>Номер записи в источнике.</summary>
    public int SourceId { get; set; }

    /// <summary>С какой своей записью совпала.</summary>
    public int? LocalId { get; set; }

    /// <summary>Чем совпала — словами.</summary>
    public required string Reason { get; set; }

    /// <summary>Номер дела или имя файла источника — для очереди (под допуском по грифу).</summary>
    public string? Label { get; set; }

    /// <summary>Состояние.</summary>
    public ExchangeConflictStatus Status { get; set; }

    /// <summary>Гриф записи пакета.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение записи — своё (сопоставленное по коду).</summary>
    public int DivisionId { get; set; }

    /// <summary>
    /// Описание записи источника для очереди (дело — номер, наименование, вид, статус, ГУ и № задания, дата; носитель — имя
    /// файла, вид, объём, дата съёмки). Видно только тем, кому доступна строка по грифу и подразделению.
    /// </summary>
    public string? Details { get; set; }

    /// <summary>Решение Руководителя; <see langword="null"/> — не решён.</summary>
    public ExchangeConflictDecision? Decision { get; set; }

    /// <summary>Своя запись для объединения (по умолчанию — совпавшая, <see cref="LocalId"/>).</summary>
    public int? DecisionLocalId { get; set; }

    /// <summary>Номер нового дела, если совпал номер в подразделении («создать новое»).</summary>
    public string? DecisionNumber { get; set; }

    /// <summary>Комментарий Руководителя к решению.</summary>
    public string? DecisionComment { get; set; }

    /// <summary>Кто решил (Руководитель).</summary>
    public int? DecidedByUserId { get; set; }

    /// <summary>Когда решено (UTC).</summary>
    public DateTime? DecidedAt { get; set; }

    /// <summary>Когда решение применено приёмом (UTC).</summary>
    public DateTime? AppliedAt { get; set; }

    /// <summary>Каким приёмом применено.</summary>
    public int? AppliedImportId { get; set; }

    /// <summary>Вид записи конфликта изменений (ADR-0068 v0.4); у конфликтов дела и носителя — <see cref="ExchangeRecordKind.None"/>.</summary>
    public ExchangeRecordKind RecordKind { get; set; }

    /// <summary>
    /// Поля конфликта изменений, JSON (<see cref="Services.ExchangeFieldChange"/>): было, у источника, у получателя — значения и
    /// текст для экрана. Значения источника отсюда записывает решение «принять версию источника».
    /// </summary>
    public string? Changes { get; set; }
}

/// <summary>Состояние эталона или появления из пакета, ждущего лица в индексации получателя (ADR-0068 v0.3).</summary>
public enum ExchangePendingFaceStatus : short
{
    /// <summary>Ждёт: дело, объект или носитель ещё не приняты либо носитель ещё не проиндексирован.</summary>
    Pending = 1,

    /// <summary>Перенесено: запись заведена у получателя (<see cref="ExchangePendingFace.LocalId"/>).</summary>
    Linked = 2,

    /// <summary>Носитель проиндексирован, но лица на месте источника не нашлось — повторится после переиндексации.</summary>
    NotFound = 3,

    /// <summary>Дело или носитель пропущены по решению Руководителя — запись не переносится.</summary>
    Skipped = 4,
}

/// <summary>
/// Эталон или появление пакета, ждущее лица в индексации получателя (ТФ-ОБМ-02, ADR-0068 v0.3): лицо получателя находится
/// по месту в кадре источника (таймкод и перекрытие рамки) после того, как его носитель проиндексирован. Строка хранит
/// запись пакета (<see cref="Payload"/>) с грифом записи — как перенесённые записи, в базе получателя.
/// </summary>
public class ExchangePendingFace : BaseEntity
{
    /// <summary>Экземпляр-источник.</summary>
    public required string SourceInstance { get; set; }

    /// <summary>Вид: <see cref="ExchangeRecordKind.ReferencePhoto"/> или <see cref="ExchangeRecordKind.Appearance"/>.</summary>
    public ExchangeRecordKind Kind { get; set; }

    /// <summary>Номер записи в источнике.</summary>
    public int SourceId { get; set; }

    /// <summary>Каким приёмом записана (последним).</summary>
    public int ImportId { get; set; }

    /// <summary>Имя пакета — для провенанса появления.</summary>
    public required string PackageName { get; set; }

    /// <summary>Запись пакета с лицом и именами сотрудников, JSON.</summary>
    public required string Payload { get; set; }

    /// <summary>Состояние.</summary>
    public ExchangePendingFaceStatus Status { get; set; }

    /// <summary>Чего ждёт или почему не перенесено — словами для Администратора (без содержимого записи).</summary>
    public string? Reason { get; set; }

    /// <summary>Заведённая у получателя запись (эталон или появление).</summary>
    public int? LocalId { get; set; }

    /// <summary>
    /// Время индексации носителя, по которой последний раз искали лицо: «лицо не найдено» и «индексация не удалась»
    /// перепроверяются, только когда носитель проиндексирован заново.
    /// </summary>
    public DateTime? CheckedIndexAt { get; set; }

    /// <summary>Гриф записи пакета.</summary>
    public short Classification { get; set; }
}
