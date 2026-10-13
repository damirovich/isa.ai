namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Пределы длин полей импорта — общие для проверки описи и схемы хранения (ADR-0041).</summary>
public static class MediaImportLimits
{
    /// <summary>Путь файла относительно папки архива.</summary>
    public const int RelativePathMaxLength = 1000;

    /// <summary>Полный путь папки архива.</summary>
    public const int FolderPathMaxLength = 1000;

    /// <summary>Причина отказа строки или остановки задания.</summary>
    public const int ReasonMaxLength = 1000;
}

/// <summary>Состояние задания пакетного импорта архива (ADR-0041).</summary>
public enum MediaImportJobStatus
{
    /// <summary>Ждёт своей очереди: задания выполняются по одному, в порядке запуска.</summary>
    Queued = 0,

    /// <summary>Идёт приём файлов.</summary>
    Running = 1,

    /// <summary>
    /// Остановлено оператором или системой (утрата права или допуска, каталог исключён из настройки); причина —
    /// в <see cref="MediaImportJobSummary.StopReason"/>. Продолжается с того же места.
    /// </summary>
    Stopped = 2,

    /// <summary>Все строки разобраны: приняты, дубликаты или отказ. Обработка поданных носителей может ещё идти.</summary>
    Completed = 3,
}

/// <summary>Состояние строки задания — одного файла архива (ADR-0041).</summary>
public enum MediaImportItemStatus
{
    /// <summary>Ждёт приёма.</summary>
    Pending = 0,

    /// <summary>Принимается сейчас. После аварии или остановки возвращается в <see cref="Pending"/>.</summary>
    InProgress = 1,

    /// <summary>Принят: создан новый носитель и привязан к делу.</summary>
    Accepted = 2,

    /// <summary>Такой файл уже хранится в подразделении с тем же грифом: привязан существующий носитель (ТФ-МЕД-04).</summary>
    Duplicate = 3,

    /// <summary>Не принят; причина — в строке.</summary>
    Rejected = 4,
}

/// <summary>
/// Подача принятого носителя архива в общую очередь обработки (ADR-0041 п. 7): не больше заданного числа носителей
/// архива в очереди одновременно, чтобы новые материалы следователей не ждали архив.
/// </summary>
public enum MediaImportProcessingState
{
    /// <summary>Обработка от задания не нужна: строка ещё не принята, отказ или дубликат уже существующего носителя.</summary>
    None = 0,

    /// <summary>Носитель принят и ждёт подачи в очередь.</summary>
    Waiting = 1,

    /// <summary>Подан в очередь: ждём завершения его задач.</summary>
    Queued = 2,

    /// <summary>Задачи завершились — успешно или с ошибкой; итог виден в карточке носителя.</summary>
    Done = 3,
}

/// <summary>
/// Строка нового задания — файл, прошедший проверку описи. Гриф и подразделение — ДЕЛА, подтверждённые оператором
/// (ТБ-024): при приёме файла они сверяются с текущими значениями дела.
/// </summary>
/// <param name="RowNumber">Номер строки описи (с заголовком — строка 1), для сообщений оператору.</param>
/// <param name="RelativePath">Путь файла относительно папки задания, как в описи (разделитель приведён к «/»).</param>
/// <param name="Kind">Вид носителя по расширению.</param>
/// <param name="ContentType">MIME-тип по расширению (семейство затем сверяется по содержимому, ТС-010).</param>
/// <param name="ByteSize">Размер файла на проверке, байт.</param>
/// <param name="CaseId">Дело (непрозрачный идентификатор профиля).</param>
/// <param name="CaseNumber">Номер дела — для отчёта.</param>
/// <param name="Classification">Подтверждённый гриф дела.</param>
/// <param name="DivisionId">Подтверждённое подразделение дела.</param>
/// <param name="CapturedAt">Дата съёмки из описи; <see langword="null"/> — будет взята из метаданных файла.</param>
/// <param name="CapturedDateOnly">В описи была только дата, без времени (взято 00:00 местного времени).</param>
/// <param name="Source">Источник (свободный текст).</param>
/// <param name="Place">Место (на привязке к делу).</param>
/// <param name="ExpectedSha256">Контрольная сумма из описи (hex, верхний регистр) или <see langword="null"/>.</param>
public sealed record MediaImportItemDraft(
    int RowNumber,
    string RelativePath,
    MediaKind Kind,
    string ContentType,
    long ByteSize,
    int CaseId,
    string CaseNumber,
    short Classification,
    int DivisionId,
    DateTimeOffset? CapturedAt,
    bool CapturedDateOnly,
    string? Source,
    string? Place,
    string? ExpectedSha256);

/// <summary>Новое задание импорта (ADR-0041).</summary>
/// <param name="FolderPath">Полный путь папки, относительно которой заданы пути описи.</param>
/// <param name="ManifestFileName">Имя файла описи (для отчёта).</param>
/// <param name="ManifestSha256">SHA-256 описи — в журнал и отчёт (цепочка хранения, ТБ-077).</param>
/// <param name="OperatorUserId">Кто запустил: от его имени и под его допуском принимается каждый файл.</param>
/// <param name="Items">Строки задания.</param>
public sealed record MediaImportJobDraft(
    string FolderPath,
    string ManifestFileName,
    string ManifestSha256,
    int OperatorUserId,
    IReadOnlyList<MediaImportItemDraft> Items);

/// <summary>Сводка задания: состояние и счётчики (без содержимого строк).</summary>
/// <param name="JobId">Задание.</param>
/// <param name="CreatedAtUtc">Когда запущено.</param>
/// <param name="OperatorUserId">Кто запустил.</param>
/// <param name="FolderPath">Папка архива.</param>
/// <param name="ManifestFileName">Имя описи.</param>
/// <param name="ManifestSha256">SHA-256 описи.</param>
/// <param name="Status">Состояние.</param>
/// <param name="StopReason">Почему остановлено (при <see cref="MediaImportJobStatus.Stopped"/>).</param>
/// <param name="FinishedAtUtc">Когда разобрана последняя строка.</param>
/// <param name="Total">Строк всего.</param>
/// <param name="Accepted">Принято новых носителей.</param>
/// <param name="Duplicates">Дубликатов.</param>
/// <param name="Rejected">Отказов.</param>
/// <param name="BytesTotal">Объём всех строк, байт.</param>
/// <param name="BytesDone">Объём разобранных строк (принятых, дубликатов и отказов), байт.</param>
/// <param name="ProcessingWaiting">Носителей ждут подачи в обработку.</param>
/// <param name="ProcessingQueued">Носителей в обработке.</param>
/// <param name="ProcessingDone">Носителей обработано.</param>
public sealed record MediaImportJobSummary(
    int JobId,
    DateTime CreatedAtUtc,
    int OperatorUserId,
    string FolderPath,
    string ManifestFileName,
    string ManifestSha256,
    MediaImportJobStatus Status,
    string? StopReason,
    DateTime? FinishedAtUtc,
    int Total,
    int Accepted,
    int Duplicates,
    int Rejected,
    long BytesTotal,
    long BytesDone,
    int ProcessingWaiting,
    int ProcessingQueued,
    int ProcessingDone)
{
    /// <summary>Строк разобрано (принято, дубликатов, отказов).</summary>
    public int Processed => Accepted + Duplicates + Rejected;
}

/// <summary>Строка задания для показа и отчёта.</summary>
/// <param name="ItemId">Строка задания.</param>
/// <param name="RowNumber">Строка описи.</param>
/// <param name="RelativePath">Путь файла относительно папки задания.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="CaseNumber">Номер дела.</param>
/// <param name="ByteSize">Размер, байт.</param>
/// <param name="Status">Состояние.</param>
/// <param name="Reason">Причина отказа.</param>
/// <param name="AssetId">Носитель (принят или дубликат).</param>
/// <param name="ContentSha256">SHA-256, посчитанный при приёме.</param>
/// <param name="ExpectedSha256">SHA-256 из описи.</param>
/// <param name="Processing">Подача в обработку.</param>
/// <param name="CapturedAt">Дата съёмки из описи.</param>
/// <param name="Source">Источник из описи.</param>
/// <param name="Place">Место из описи.</param>
public sealed record MediaImportItemView(
    int ItemId,
    int RowNumber,
    string RelativePath,
    int CaseId,
    string CaseNumber,
    long ByteSize,
    MediaImportItemStatus Status,
    string? Reason,
    int? AssetId,
    string? ContentSha256,
    string? ExpectedSha256,
    MediaImportProcessingState Processing,
    DateTimeOffset? CapturedAt,
    string? Source,
    string? Place);

/// <summary>Страница строк задания (под решёткой допуска смотрящего, ТБ-020).</summary>
/// <param name="Rows">Строки страницы.</param>
/// <param name="Total">Строк в отборе, видимых смотрящему.</param>
/// <param name="Hidden">Строк задания вне допуска смотрящего — показываются только числом.</param>
public sealed record MediaImportItemPage(IReadOnlyList<MediaImportItemView> Rows, int Total, int Hidden);

/// <summary>Задание, которое фоновый исполнитель взял в работу.</summary>
/// <param name="JobId">Задание.</param>
/// <param name="FolderPath">Папка архива.</param>
/// <param name="OperatorUserId">От чьего имени принимаются файлы.</param>
public sealed record MediaImportJobRun(int JobId, string FolderPath, int OperatorUserId);

/// <summary>Строка, взятая исполнителем в приём (переведена в <see cref="MediaImportItemStatus.InProgress"/>).</summary>
/// <param name="ItemId">Строка задания.</param>
/// <param name="JobId">Задание.</param>
/// <param name="Draft">Реквизиты строки, как их записали при запуске.</param>
public sealed record MediaImportWorkItem(int ItemId, int JobId, MediaImportItemDraft Draft);

/// <summary>Итог приёма строки.</summary>
/// <param name="Status">Принят, дубликат или отказ.</param>
/// <param name="Reason">Причина отказа.</param>
/// <param name="AssetId">Носитель.</param>
/// <param name="ContentSha256">SHA-256, посчитанный при приёме.</param>
/// <param name="Processing">Нужна ли подача носителя в обработку.</param>
public sealed record MediaImportItemOutcome(
    MediaImportItemStatus Status,
    string? Reason,
    int? AssetId,
    string? ContentSha256,
    MediaImportProcessingState Processing)
{
    /// <summary>Отказ с причиной.</summary>
    public static MediaImportItemOutcome Reject(string reason) =>
        new(MediaImportItemStatus.Rejected, reason, null, null, MediaImportProcessingState.None);
}

/// <summary>Носитель архива в подаче на обработку.</summary>
/// <param name="ItemId">Строка задания.</param>
/// <param name="AssetId">Носитель.</param>
/// <param name="Kind">Вид носителя — какие конвейеры нужны.</param>
/// <param name="IndexTaskId">Фоновая задача индексации лиц, если подана.</param>
/// <param name="TranscriptTaskId">Фоновая задача расшифровки, если подана.</param>
public sealed record MediaImportFeedItem(int ItemId, int AssetId, MediaKind Kind, Guid? IndexTaskId, Guid? TranscriptTaskId);

/// <summary>Контрольная сумма файла не совпала с описью — файл в хранилище не записан (ADR-0041 п. 6).</summary>
public sealed class MediaContentHashMismatchException : Exception
{
    /// <summary>Создаёт исключение с ожидаемым и фактическим значением.</summary>
    /// <param name="expected">SHA-256 из описи.</param>
    /// <param name="actual">SHA-256 прочитанного файла.</param>
    public MediaContentHashMismatchException(string expected, string actual)
        : base($"Контрольная сумма не совпала: в описи {expected}, у файла {actual}.")
    {
        Expected = expected;
        Actual = actual;
    }

    /// <summary>Создаёт исключение без значений (для сериализации и анализаторов).</summary>
    public MediaContentHashMismatchException()
    {
        Expected = string.Empty;
        Actual = string.Empty;
    }

    /// <summary>Создаёт исключение с сообщением.</summary>
    /// <param name="message">Сообщение.</param>
    public MediaContentHashMismatchException(string message)
        : base(message)
    {
        Expected = string.Empty;
        Actual = string.Empty;
    }

    /// <summary>Создаёт исключение с сообщением и вложенным исключением.</summary>
    /// <param name="message">Сообщение.</param>
    /// <param name="innerException">Причина.</param>
    public MediaContentHashMismatchException(string message, Exception innerException)
        : base(message, innerException)
    {
        Expected = string.Empty;
        Actual = string.Empty;
    }

    /// <summary>SHA-256 из описи.</summary>
    public string Expected { get; }

    /// <summary>SHA-256 прочитанного файла.</summary>
    public string Actual { get; }
}
