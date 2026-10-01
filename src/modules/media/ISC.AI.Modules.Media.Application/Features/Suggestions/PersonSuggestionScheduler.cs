using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Media.Application.Features.Suggestions;

/// <summary>
/// Постановка сверки с эталонами фигурантов в фоновую очередь (ТФ-ПЕР-09, ADR-0035). Очередь ядра одна и
/// разбирается одним исполнителем: сверка и индексация одного носителя не идут одновременно, поэтому проверка
/// повторов в <see cref="PersonSuggester"/> не гоняется сама с собой.
/// </summary>
/// <remarks>
/// Сбой постановки не пробрасывается: вызывают его команды, которые свою запись уже сделали (эталон, основание).
/// Откатывать их из-за подсказки нельзя — сбой пишется в лог, сверку можно запустить кнопкой «Сверить сейчас».
/// </remarks>
public sealed partial class PersonSuggestionScheduler(
    IBackgroundTaskQueue queue,
    MediaSearchOptions options,
    ILogger<PersonSuggestionScheduler> logger) : IPersonSuggestionScheduler
{
    /// <summary>Название задачи сверки материалов дела — видно в списке фоновых задач.</summary>
    public const string CaseSweepKind = "Сверка материалов дела с эталонами фигурантов";

    /// <summary>Название задачи сверки одного носителя.</summary>
    public const string AssetKind = "Сверка носителя с эталонами фигурантов";

    /// <inheritdoc />
    public Task<Guid?> ScheduleCaseSweepAsync(int caseId, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            CaseSweepKind,
            "дело " + caseId.ToString(CultureInfo.InvariantCulture),
            async (sp, ct) => await sp.GetRequiredService<IPersonSuggester>().SuggestForCaseAsync(caseId, ct),
            cancellationToken);

    /// <inheritdoc />
    public Task<Guid?> ScheduleAssetAsync(int assetId, SuggestionTrigger trigger, CancellationToken cancellationToken = default) =>
        EnqueueAsync(
            AssetKind,
            "носитель " + assetId.ToString(CultureInfo.InvariantCulture),
            async (sp, ct) => await sp.GetRequiredService<IPersonSuggester>().SuggestAsync(assetId, trigger, ct),
            cancellationToken);

    private async Task<Guid?> EnqueueAsync(
        string kind, string target, Func<IServiceProvider, CancellationToken, Task> work, CancellationToken cancellationToken)
    {
        if (!options.AutoSuggestEnabled)
        {
            return null; // функция выключена настройкой — ставить нечего
        }

        try
        {
            // Делегат захватывает только примитивы: scope вызвавшего запроса к моменту выполнения уже закрыт.
            return await queue.EnqueueAsync(kind, work, cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogScheduleFailed(logger, exception, target);
            return null;
        }
    }

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Сверку с эталонами фигурантов ({Target}) не удалось поставить в очередь; её можно запустить кнопкой «Сверить сейчас».")]
    private static partial void LogScheduleFailed(ILogger logger, Exception exception, string target);
}
