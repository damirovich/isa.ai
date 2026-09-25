using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Modules.Media.Data;

/// <summary>
/// Восстановление статусов носителей после перезапуска хоста: задачи фоновой очереди живут в памяти и при
/// перезапуске пропадают, а статус «в очереди / в работе» в БД остаётся (ADR-0026; тот же принцип, что у
/// восстановления осиротевших задач ядра, §5.1.4.5).
/// </summary>
public sealed partial class MediaStore
{
    /// <summary>Причина, записываемая расшифровке, прерванной перезапуском системы.</summary>
    public const string InterruptedTranscriptionReason =
        "прервано перезапуском системы — нажмите «Расшифровать заново»";

    /// <summary>Причина, записываемая индексации лиц, прерванной перезапуском системы.</summary>
    public const string InterruptedIndexingReason =
        "прервано перезапуском системы — нажмите «Переиндексировать»";

    /// <inheritdoc />
    /// <remarks>
    /// Статусы переводятся в «ошибка», а не ставятся в очередь заново: запись, на которой прежний процесс упал,
    /// уронила бы и новый, и так по кругу. «Ошибка» с причиной честна и снимается кнопкой повтора. Каждый
    /// конвейер — одним UPDATE по статусу: носителей в работе может быть сколько угодно, построчного обхода нет.
    /// </remarks>
    public async Task<InterruptedWorkRecovery> RecoverInterruptedAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

        var transcriptions = await db.Assets
            .Where(a => a.TranscriptStatus == TranscriptStatus.Pending || a.TranscriptStatus == TranscriptStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.TranscriptStatus, TranscriptStatus.Failed)
                .SetProperty(a => a.TranscriptError, InterruptedTranscriptionReason), cancellationToken);

        // Только «обрабатывается»: его ставит сам конвейер ПОСЛЕ всех проверок. «Загружен» — не трогаем: в нём же
        // остаётся носитель, которому конвейер отказал по закрытому делу (ТБ-074), и это не сбой.
        var indexings = await db.Assets
            .Where(a => a.IndexStatus == MediaIndexStatus.Processing)
            .ExecuteUpdateAsync(s => s
                .SetProperty(a => a.IndexStatus, MediaIndexStatus.Failed)
                .SetProperty(a => a.IndexError, InterruptedIndexingReason), cancellationToken);

        return new InterruptedWorkRecovery(transcriptions, indexings);
    }
}
