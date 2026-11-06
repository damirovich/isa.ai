using ISC.AI.Abstractions.Entities;

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

    /// <summary>Конфликтов (дела и носители) — ждут очереди Руководителя.</summary>
    public int Conflicts { get; set; }

    /// <summary>Отложено до этапа 3 — эталоны и появления (им нужна индексация получателя).</summary>
    public int Deferred { get; set; }

    /// <summary>Носителей пакета без файла и ещё не принятых — файл ушёл прежним пакетом.</summary>
    public int MissingFiles { get; set; }

    /// <summary>Файлов, отклонённых общим приёмом «Медиа» (формат, размер, хеш оригинала).</summary>
    public int Rejected { get; set; }

    /// <summary>Причина сбоя (текст для Администратора, без содержимого пакета).</summary>
    public string? Error { get; set; }
}

/// <summary>Вид записи в карте «запись источника → своя запись».</summary>
public enum ExchangeRecordKind : short
{
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
}

/// <summary>Вид конфликта приёма.</summary>
public enum ExchangeConflictKind : short
{
    /// <summary>У получателя уже есть дело с той же парой «ГУ + № задания» или с тем же номером в подразделении.</summary>
    Case = 1,

    /// <summary>У получателя уже есть носитель с тем же файлом (ТФ-МЕД-04), не принятый из этого источника.</summary>
    Asset = 2,
}

/// <summary>Состояние конфликта.</summary>
public enum ExchangeConflictStatus : short
{
    /// <summary>Ждёт решения Руководителя (очередь — этап 3).</summary>
    Pending = 1,

    /// <summary>
    /// Снят: повторный приём принял запись — совпадения больше нет (своё дело переименовали, свой носитель уничтожили).
    /// </summary>
    Cleared = 2,
}

/// <summary>
/// Конфликт приёма (ТФ-ОБМ-02): запись пакета совпала с записью получателя — решать, объединить, завести новую или
/// пропустить, Руководителю (не Администратору, ТБ-079; очередь — этап 3). Запись и всё, что от неё зависит, до решения не
/// переносится. Гриф и подразделение — записи: очередь показывает конфликт только тем, кому он доступен.
/// </summary>
public class ExchangeImportConflict : BaseEntity
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
}
