using ISC.AI.Profile.Investigation.Domain.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Офис-источник в настройке получателя: номер и отпечаток его ключа подписи (или что с ключом не так).</summary>
/// <param name="InstanceId">Номер экземпляра-источника.</param>
/// <param name="Fingerprint">Отпечаток ключа подписи; <see langword="null"/> — ключ не прочитан.</param>
/// <param name="Problem">Что не так с ключом; <see langword="null"/> — ключ годен.</param>
public sealed record ExchangeSourceInfo(string InstanceId, string? Fingerprint, string? Problem);

/// <summary>Готовность экземпляра к приёму пакетов: что настроено и что нет.</summary>
/// <param name="InstanceId">Номер этого экземпляра.</param>
/// <param name="ImportFolder">Носитель с пакетами (точка монтирования).</param>
/// <param name="ReceivingKeyFingerprint">Закреплённый отпечаток ключа приёма (сам ключ — под паролем хранителя).</param>
/// <param name="Sources">Настроенные офисы-источники.</param>
/// <param name="Problems">Чего не хватает — текстом для Администратора.</param>
public sealed record ExchangeImportReadiness(
    string? InstanceId,
    string? ImportFolder,
    string? ReceivingKeyFingerprint,
    IReadOnlyList<ExchangeSourceInfo> Sources,
    IReadOnlyList<string> Problems)
{
    /// <summary>Можно принимать.</summary>
    public bool Ready => Problems.Count == 0;
}

/// <summary>Пакет на носителе — по манифесту (после проверки подписи), без содержимого.</summary>
/// <param name="FolderName">Имя каталога пакета.</param>
/// <param name="SourceInstance">Источник (до проверки подписи — как его называет манифест).</param>
/// <param name="Period">Период, «гггг-ММ».</param>
/// <param name="CreatedAt">Когда сформирован.</param>
/// <param name="Marking">Маркировка словами.</param>
/// <param name="Counts">Счётчики манифеста.</param>
/// <param name="Bytes">Объём файлов пакета, байт.</param>
/// <param name="Problem">Почему пакет нельзя принять; <see langword="null"/> — можно.</param>
/// <param name="LastStatus">Итог последнего приёма этого пакета.</param>
/// <param name="LastImportAt">Когда его принимали последний раз (UTC).</param>
/// <param name="DecisionsAwaiting">
/// Решения Руководителя по конфликтам, найденным этим пакетом, которые ждут повторного приёма (ADR-0068 v0.3) — только число.
/// </param>
public sealed record ExchangePackageRow(
    string FolderName,
    string? SourceInstance,
    string? Period,
    DateTimeOffset? CreatedAt,
    string? Marking,
    IReadOnlyDictionary<string, int>? Counts,
    long? Bytes,
    string? Problem,
    ExchangeImportStatus? LastStatus,
    DateTime? LastImportAt,
    int DecisionsAwaiting = 0)
{
    /// <summary>Пакет можно принять.</summary>
    public bool CanImport => Problem is null;
}

/// <summary>Строка журнала приёмов — без содержимого пакета (ТБ-079).</summary>
public sealed record ExchangeImportRow(
    int Id,
    DateTime StartedAt,
    DateTime? FinishedAt,
    ExchangeImportStatus Status,
    int StartedByUserId,
    string PackageName,
    string? SourceInstance,
    string? Period,
    bool GatewayConfirmed,
    int CasesCreated,
    int PersonsCreated,
    int EventsCreated,
    int ReportsCreated,
    int AssetsCreated,
    int FilesReceived,
    long BytesReceived,
    int AlreadyImported,
    int Conflicts,
    int Deferred,
    int MissingFiles,
    int Rejected,
    short? Classification,
    string? Error,
    int Merged = 0,
    int SkippedByDecision = 0,
    int FacesLinked = 0,
    int FacesNotFound = 0,
    bool? ReconciliationOk = null,
    string? Reconciliation = null,
    int RecordsUpdated = 0,
    int UpdateConflicts = 0);

/// <summary>
/// Эталоны и появления из пакетов, ждущие лица в индексации получателя (ADR-0068 v0.3) — только числа, без содержимого.
/// </summary>
/// <param name="Waiting">Ждут: дело, объект или носитель ещё не приняты либо носитель индексируется.</param>
/// <param name="NotFound">Лицо не найдено — повторится после переиндексации носителя.</param>
/// <param name="Linked">Перенесено.</param>
/// <param name="Skipped">Пропущено вместе с делом или носителем по решению Руководителя.</param>
public sealed record ExchangeFaceBacklog(int Waiting, int NotFound, int Linked, int Skipped);

/// <summary>Экран приёма: готовность, пакеты на носителе, журнал приёмов и ход текущего.</summary>
/// <param name="Readiness">Готовность.</param>
/// <param name="Packages">Пакеты на носителе.</param>
/// <param name="Recent">Последние приёмы.</param>
/// <param name="RunningStage">Ход текущего приёма словами; <see langword="null"/> — приёма нет.</param>
/// <param name="ConflictsPending">Конфликтов ждут решения Руководителя (число; сами конфликты — в его очереди).</param>
/// <param name="DecisionsAwaiting">Решений Руководителя ждут повторного приёма пакета.</param>
/// <param name="Faces">Эталоны и появления, ждущие лица.</param>
public sealed record ExchangeImportOverview(
    ExchangeImportReadiness Readiness,
    IReadOnlyList<ExchangePackageRow> Packages,
    IReadOnlyList<ExchangeImportRow> Recent,
    string? RunningStage,
    int ConflictsPending = 0,
    int DecisionsAwaiting = 0,
    ExchangeFaceBacklog? Faces = null);

/// <summary>Итог запуска приёма.</summary>
/// <param name="ImportId">Номер приёма; <see langword="null"/> — не запущен.</param>
/// <param name="Error">Почему не запущен.</param>
public sealed record ExchangeImportStartResult(int? ImportId, string? Error);

/// <summary>
/// Обмен между офисами — сторона получателя (ТФ-ОБМ-02, ADR-0068 v0.2): готовность, пакеты на носителе, журнал приёмов,
/// запуск фонового приёма. Реализует слой данных профиля.
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ (ТБ-079, ТБ-083). Пароль ключа приёма передаётся только в <see cref="StartImportAsync"/> и живёт в памяти
/// задания до расшифровки — в базу, журнал аудита и логи он не попадает. Без отметки «пакет проверен на шлюзе» приём не
/// начинается. Записи пакета пишутся системной операцией мимо допуска Администратора, а Администратор видит только
/// манифест и счётчики. Право на действие проверяет сценарий (матрица «Обмен между офисами»), порт его не проверяет.
/// </remarks>
public interface IExchangeImport
{
    /// <summary>Готовность, пакеты на носителе, последние приёмы и ход текущего.</summary>
    Task<ExchangeImportOverview> GetImportOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает приём пакета в фоне. Один приём в каждый момент; не готов экземпляр, нет отметки шлюза, пакет нельзя
    /// принять или пароль ключа приёма неверен — отказ с причиной.
    /// </summary>
    Task<ExchangeImportStartResult> StartImportAsync(
        string packageName, bool gatewayConfirmed, int userId, string receivingKeyPassword, CancellationToken cancellationToken = default);
}
