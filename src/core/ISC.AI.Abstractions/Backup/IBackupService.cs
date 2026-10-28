namespace ISC.AI.Abstractions.Backup;

/// <summary>Состояние прогона резервной копии.</summary>
public enum BackupRunStatus
{
    /// <summary>Копия пишется.</summary>
    Running = 1,

    /// <summary>Копия готова.</summary>
    Succeeded = 2,

    /// <summary>Копия не удалась (причина в строке).</summary>
    Failed = 3,

    /// <summary>Прервана перезапуском хоста.</summary>
    Interrupted = 4,
}

/// <summary>Итог проверки манифеста копии.</summary>
public enum BackupVerifyStatus
{
    /// <summary>Проверка идёт.</summary>
    Running = 1,

    /// <summary>Копия цела.</summary>
    Ok = 2,

    /// <summary>Найдены расхождения (в пояснении).</summary>
    Problems = 3,
}

/// <summary>Прогон резервной копии из журнала <c>core.backup_run</c>.</summary>
/// <param name="Id">Номер.</param>
/// <param name="StartedAtUtc">Начало.</param>
/// <param name="FinishedAtUtc">Окончание; <see langword="null"/> — идёт или прервана.</param>
/// <param name="Status">Состояние.</param>
/// <param name="StartedByUserId">Кто запустил.</param>
/// <param name="FolderName">Каталог копии на носителе.</param>
/// <param name="Databases">Сколько баз в копии.</param>
/// <param name="StorageFiles">Файлов в копии — всех каталогов файлов (хранилище ядра и пакетов).</param>
/// <param name="StorageBytes">Объём этих файлов, байт.</param>
/// <param name="BytesWritten">Записано на носитель, байт (во время копии — ход).</param>
/// <param name="Error">Причина сбоя.</param>
/// <param name="VerifyStatus">Итог последней проверки манифеста; <see langword="null"/> — не проверялась.</param>
/// <param name="VerifiedAtUtc">Когда проверялась.</param>
/// <param name="VerifyNote">Расхождения проверки.</param>
public sealed record BackupRunRow(
    int Id,
    DateTime StartedAtUtc,
    DateTime? FinishedAtUtc,
    BackupRunStatus Status,
    int? StartedByUserId,
    string? FolderName,
    int Databases,
    int? StorageFiles,
    long? StorageBytes,
    long BytesWritten,
    string? Error,
    BackupVerifyStatus? VerifyStatus,
    DateTime? VerifiedAtUtc,
    string? VerifyNote);

/// <summary>Состояние резервного копирования для экрана Администратора (ТФ-АДМ-04, ADR-0063).</summary>
/// <param name="Problems">Что не настроено (пусто — копию можно делать).</param>
/// <param name="TargetFolder">Каталог носителя.</param>
/// <param name="TargetAvailable">Носитель подключён (каталог доступен).</param>
/// <param name="TargetFreeBytes">Свободно на носителе, байт.</param>
/// <param name="EstimatedBytes">Оценка объёма копии (базы + каталоги файлов), байт; <see langword="null"/> — не оценить.</param>
/// <param name="KeyFingerprint">Отпечаток ключа шифрования.</param>
/// <param name="ReminderDays">Через сколько дней без успешной копии напоминать.</param>
/// <param name="Runs">Последние прогоны, новые первыми.</param>
public sealed record BackupOverview(
    IReadOnlyList<string> Problems,
    string? TargetFolder,
    bool TargetAvailable,
    long? TargetFreeBytes,
    long? EstimatedBytes,
    string? KeyFingerprint,
    int ReminderDays,
    IReadOnlyList<BackupRunRow> Runs)
{
    /// <summary>Копию можно делать: настроено и носитель подключён.</summary>
    public bool Ready => Problems.Count == 0 && TargetAvailable;
}

/// <summary>Итог запроса на действие: номер прогона или текст отказа.</summary>
/// <param name="RunId">Прогон.</param>
/// <param name="Refusal">Почему не запущено.</param>
public sealed record BackupActionResult(int? RunId, string? Refusal);

/// <summary>
/// ПОРТ ЯДРА: резервные копии (ТФ-АДМ-04, ТБ-082, ADR-0063) — состояние, запуск копии и проверка манифеста последней
/// копии фоновыми задачами. Право (кто вправе) проверяет сценарий пакета администрирования через порт профиля.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-079): копия содержит материалы, но только зашифрованными открытым ключом Заказчика — закрытого ключа в
/// системе нет, расшифровать копию Администратор не может. Восстановление — офлайн программой ISC.AI.Backup.Restore.
/// </remarks>
public interface IBackupService
{
    /// <summary>Настройка, носитель, оценка объёма и последние прогоны.</summary>
    Task<BackupOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>Ставит копию в очередь. Вторая, пока идёт первая, не запускается.</summary>
    Task<BackupActionResult> StartAsync(int startedByUserId, CancellationToken cancellationToken = default);

    /// <summary>Ставит в очередь проверку манифеста последней готовой копии на носителе.</summary>
    Task<BackupActionResult> VerifyLatestAsync(int verifiedByUserId, CancellationToken cancellationToken = default);
}
