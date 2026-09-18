using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.Admin.Application.Features.Clearances;

/// <summary>
/// Разовая проверка на старте: нет ли в допусках (<c>core.clearance</c>) номеров подразделений,
/// которых НЕТ в справочнике профиля (<see cref="IDivisionCatalog"/>).
/// </summary>
/// <remarks>
/// Словарь номеров подразделений ОБЯЗАН быть общим для решётки доступа ядра и справочника профиля,
/// но ничем не проверяется: обе стороны наполняются независимо, а на боевом контуре допуски
/// исторически правились SQL'ем. Расхождение не даёт ни ошибки, ни отказа — оно проявляется
/// у пользователя как «мне ничего не видно» и «не могу зарегистрировать документ, хотя допуск
/// выдан», то есть в самой неудобной для диагностики форме. Проверка переводит это в явное
/// предупреждение при старте; тот же разбор показывает экран «Допуски пользователей».
///
/// Только ЛОГ, никаких изменений данных и никакого отказа старта: это гигиена конфигурации, а не
/// нарушение инварианта безопасности — лишний номер не РАСШИРЯЕТ доступ, он просто мёртв.
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
            var divisions = scope.ServiceProvider.GetRequiredService<IDivisionCatalog>();

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
                ClearanceConsistencyLog.UnknownDivisions(logger, userId, string.Join(", ", unknown));
            }

            ClearanceConsistencyLog.Summary(logger, broken.Count);
        }
        catch (OperationCanceledException)
        {
            // Штатная остановка приложения.
        }
        catch (Exception ex)
        {
            // Диагностическая проверка не имеет права ронять хост.
            ClearanceConsistencyLog.CheckFailed(logger, ex);
        }
    }
}

/// <summary>Строго-типизированные сообщения проверки согласованности (LoggerMessage — CA1848).</summary>
internal static partial class ClearanceConsistencyLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Допуск пользователя {UserId} ссылается на подразделения, которых нет в справочнике: {Unknown}. Доступа они не дают.")]
    public static partial void UnknownDivisions(ILogger logger, int userId, string unknown);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Сверка допусков со справочником подразделений: расхождения у {Count} пользователей — см. экран «Допуски пользователей»")]
    public static partial void Summary(ILogger logger, int count);

    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Сверка допусков со справочником подразделений не выполнена — старт приложения не прерван")]
    public static partial void CheckFailed(ILogger logger, Exception exception);
}
