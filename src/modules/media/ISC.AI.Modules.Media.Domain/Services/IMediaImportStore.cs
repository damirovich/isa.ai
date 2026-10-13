using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт хранилища заданий пакетного импорта архива (ADR-0041): задания и их строки (<c>media.import_job</c>,
/// <c>media.import_item</c>) переживают перезапуск сервера, поэтому импорт продолжается с того же места, а подача
/// принятых носителей в обработку восстанавливается сама. Реализация — <c>Media.Data</c>.
/// </summary>
/// <remarks>
/// Строка задания несёт гриф и подразделение своего дела (ТБ-070) и читается под решёткой допуска смотрящего
/// (ТБ-020): путь файла и номер дела вне допуска не показываются. Сводка задания — только числа и папка архива.
/// Права (кто видит экран, кто запускает и останавливает) проверяет вызывающий сценарий.
/// </remarks>
public interface IMediaImportStore
{
    // --- Экран ---

    /// <summary>Создаёт задание в состоянии <see cref="MediaImportJobStatus.Queued"/> со всеми строками одной транзакцией.</summary>
    /// <returns>Идентификатор задания.</returns>
    Task<int> CreateJobAsync(MediaImportJobDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Последние задания, новые первыми.</summary>
    Task<IReadOnlyList<MediaImportJobSummary>> ListJobsAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>Сводка задания или <see langword="null"/>.</summary>
    Task<MediaImportJobSummary?> GetJobAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Страница строк задания под решёткой допуска смотрящего (ТБ-020/021): строки с грифом выше допуска или
    /// подразделением вне его не выдаются, а считаются в <see cref="MediaImportItemPage.Hidden"/>.
    /// </summary>
    /// <param name="jobId">Задание.</param>
    /// <param name="status">Отбор по состоянию; <see langword="null"/> — все.</param>
    /// <param name="access">Допуск смотрящего.</param>
    /// <param name="skip">Сколько строк пропустить.</param>
    /// <param name="take">Сколько строк выдать.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<MediaImportItemPage> ListItemsAsync(
        int jobId, MediaImportItemStatus? status, AccessContext access, int skip, int take,
        CancellationToken cancellationToken = default);

    /// <summary>Все строки задания под решёткой смотрящего, по порядку описи — для выгрузки отчёта.</summary>
    IAsyncEnumerable<MediaImportItemView> StreamItemsAsync(
        int jobId, MediaImportItemStatus? status, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Останавливает задание (<see cref="MediaImportJobStatus.Queued"/> или <see cref="MediaImportJobStatus.Running"/>
    /// → <see cref="MediaImportJobStatus.Stopped"/>) с причиной. Строка, которая принимается в этот момент, будет
    /// дочитана или возвращена в очередь исполнителем.
    /// </summary>
    /// <returns><see langword="false"/> — задания нет или оно уже остановлено или завершено.</returns>
    Task<bool> StopAsync(int jobId, string reason, CancellationToken cancellationToken = default);

    /// <summary>Возобновляет остановленное задание (<see cref="MediaImportJobStatus.Stopped"/> → <see cref="MediaImportJobStatus.Queued"/>).</summary>
    /// <returns><see langword="false"/> — задания нет или оно не остановлено.</returns>
    Task<bool> ResumeAsync(int jobId, CancellationToken cancellationToken = default);

    // --- Исполнитель: приём строк ---

    /// <summary>
    /// Восстановление при старте исполнителя: строки «принимается» возвращаются в «ждёт приёма», задания «идёт» — в
    /// «ждёт очереди» (их строки дочитает исполнитель нового процесса).
    /// </summary>
    /// <returns>Сколько строк возвращено.</returns>
    Task<int> RecoverInterruptedAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Задание в работу: уже идущее либо самое раннее ждущее (переводится в <see cref="MediaImportJobStatus.Running"/>).
    /// <see langword="null"/> — работы нет.
    /// </summary>
    Task<MediaImportJobRun?> StartNextJobAsync(CancellationToken cancellationToken = default);

    /// <summary>Текущее состояние задания или <see langword="null"/>.</summary>
    Task<MediaImportJobStatus?> GetJobStatusAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Следующая строка задания по порядку описи — переводится в <see cref="MediaImportItemStatus.InProgress"/>.
    /// <see langword="null"/> — строк, ждущих приёма, нет.
    /// </summary>
    Task<MediaImportWorkItem?> TakeNextItemAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>Записывает итог строки и обновляет счётчики задания одной транзакцией.</summary>
    Task CompleteItemAsync(int itemId, MediaImportItemOutcome outcome, CancellationToken cancellationToken = default);

    /// <summary>Возвращает строку в «ждёт приёма» (приём прерван остановкой или отменой).</summary>
    Task ReturnItemAsync(int itemId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Завершает задание, если строк, ждущих приёма или принимаемых, не осталось
    /// (<see cref="MediaImportJobStatus.Running"/> → <see cref="MediaImportJobStatus.Completed"/>).
    /// </summary>
    /// <returns><see langword="true"/> — задание завершено этим вызовом.</returns>
    Task<bool> TryCompleteJobAsync(int jobId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Носитель создан этой же строкой задания (приём был прерван после записи носителя, но до записи итога строки):
    /// тогда «дубликат» при повторе — свой же носитель, и его обработку нужно подать.
    /// </summary>
    Task<bool> IsAssetFromItemAsync(int assetId, int itemId, CancellationToken cancellationToken = default);

    // --- Исполнитель: подача в обработку ---

    /// <summary>
    /// Восстановление подачи при старте: строки «в обработке» прежнего процесса — задачи ушли вместе с очередью в
    /// памяти. Строки, у которых все поданные задачи успели завершиться успешно (<paramref name="isCompleted"/>),
    /// отмечаются обработанными, остальные возвращаются в «ждёт подачи».
    /// </summary>
    /// <returns>Сколько строк возвращено в «ждёт подачи».</returns>
    Task<int> RecoverProcessingAsync(Func<Guid, Task<bool>> isCompleted, CancellationToken cancellationToken = default);

    /// <summary>Носители архива, поданные в обработку и ещё не отмеченные обработанными.</summary>
    Task<IReadOnlyList<MediaImportFeedItem>> ListQueuedProcessingAsync(CancellationToken cancellationToken = default);

    /// <summary>Следующие носители, ждущие подачи, — по порядку заданий и строк описи.</summary>
    Task<IReadOnlyList<MediaImportFeedItem>> ListWaitingProcessingAsync(int take, CancellationToken cancellationToken = default);

    /// <summary>Отмечает носитель поданным в обработку с идентификаторами фоновых задач.</summary>
    Task MarkProcessingQueuedAsync(int itemId, Guid? indexTaskId, Guid? transcriptTaskId, CancellationToken cancellationToken = default);

    /// <summary>Отмечает обработку носителя завершённой.</summary>
    Task MarkProcessingDoneAsync(int itemId, CancellationToken cancellationToken = default);
}
