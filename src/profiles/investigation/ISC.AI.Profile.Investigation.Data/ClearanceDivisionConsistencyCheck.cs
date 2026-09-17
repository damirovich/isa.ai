using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data;

/// <summary>
/// Разовая проверка на старте: нет ли в допусках (<c>core.clearance</c>) номеров подразделений,
/// которых НЕТ в справочнике профиля (<c>investigation.division</c>).
/// </summary>
/// <remarks>
/// Словарь номеров подразделений ОБЯЗАН быть общим для решётки доступа ядра и справочника профиля
/// (см. <c>IDivisionDirectory</c>), но ничем не проверяется: обе стороны наполняются независимо.
/// Расхождение проявляется у пользователя как «мне ничего не видно» — здесь оно переводится в явное
/// предупреждение при старте. Только ЛОГ, без изменений данных и без отказа старта: лишний номер
/// доступ не РАСШИРЯЕТ, он просто мёртв.
/// </remarks>
public sealed class ClearanceDivisionConsistencyCheck(
    IServiceScopeFactory scopeFactory,
    ILogger<ClearanceDivisionConsistencyCheck> logger) : BackgroundService
{
    // Пауза старта: дождаться применения миграций — иначе проверка упрётся в отсутствующие таблицы.
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(30);

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);

            using var scope = scopeFactory.CreateScope();
            var clearances = scope.ServiceProvider.GetRequiredService<IClearanceStore>();
            var divisions = scope.ServiceProvider.GetRequiredService<IDivisionAdminStore>();

            var known = (await divisions.ListAsync(stoppingToken)).Select(d => d.Id).ToHashSet();
            var rows = await clearances.ListAsync(stoppingToken);

            // В лог идут идентификаторы, не имена: диагностики достаточно, лишних персональных данных нет.
            var broken = rows
                .Select(row => (row.UserId, Unknown: row.DivisionScope.Where(id => !known.Contains(id)).ToList()))
                .Where(item => item.Unknown.Count > 0)
                .ToList();

            if (broken.Count == 0)
            {
                return;
            }

            foreach (var (userId, unknown) in broken)
            {
                InvestigationClearanceConsistencyLog.UnknownDivisions(logger, userId, string.Join(", ", unknown));
            }

            InvestigationClearanceConsistencyLog.Summary(logger, broken.Count);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка приложения.
        }
        catch (Exception ex)
        {
            // Диагностическая проверка не имеет права ронять хост.
            InvestigationClearanceConsistencyLog.CheckFailed(logger, ex);
        }
    }
}

/// <summary>Строго-типизированные сообщения проверки согласованности (LoggerMessage — CA1848).</summary>
internal static partial class InvestigationClearanceConsistencyLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Допуск пользователя {UserId} ссылается на подразделения, которых нет в справочнике профиля «Следствие»: {Unknown}. Доступа они не дают.")]
    public static partial void UnknownDivisions(ILogger logger, int userId, string unknown);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Сверка допусков со справочником подразделений: расхождения у {Count} пользователей — см. экран «Допуски пользователей»")]
    public static partial void Summary(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Сверка допусков со справочником подразделений не выполнена — старт приложения не прерван")]
    public static partial void CheckFailed(ILogger logger, Exception exception);
}
