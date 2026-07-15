using System.Diagnostics.CodeAnalysis;

namespace ISC.AI.Abstractions.BackgroundTasks;

/// <summary>
/// Очередь фоновых ИИ-задач (Э4-20, §5.1.3.1): интеллектуальные операции (индексация, генерация, анализ)
/// выполняются асинхронно; пользователь ставит задачу и получает результат по готовности. Числовые
/// нормативы времени ИИ не задаются (§5.1.3).
/// </summary>
[SuppressMessage("Naming", "CA1711:Identifiers should not have incorrect suffix",
    Justification = "«Queue» — семантически точный суффикс: это именно очередь задач.")]
public interface IBackgroundTaskQueue
{
    /// <summary>
    /// Ставит работу в очередь, ПЕРСИСТИТ статус <see cref="BackgroundTaskStatus.Queued"/> и возвращает
    /// идентификатор задачи для отслеживания. Делегат получает per-operation scope (для scoped-сервисов)
    /// и токен отмены; исполняется фоновым воркером.
    /// </summary>
    /// <param name="kind">Вид операции (для отображения/аудита).</param>
    /// <param name="work">Работа: принимает scope-провайдер и токен отмены.</param>
    /// <param name="cancellationToken">Токен отмены постановки (не выполнения).</param>
    ValueTask<Guid> EnqueueAsync(
        string kind,
        Func<IServiceProvider, CancellationToken, Task> work,
        CancellationToken cancellationToken = default);
}
