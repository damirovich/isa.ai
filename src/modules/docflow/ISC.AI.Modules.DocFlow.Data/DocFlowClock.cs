using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Часы модуля по поясу эксплуатанта (перенос <c>SystemTimeProvider</c> СКИД). Пояс —
/// <c>DocFlow:TimeZone</c>, по умолчанию <c>Asia/Bishkek</c>. В Linux-контейнере обязателен пакет
/// tzdata (в образе СКИД уже был — учесть на этапе 9 при деплое).
/// </summary>
public sealed class DocFlowClock : IDocFlowClock
{
    private readonly TimeZoneInfo _timeZone;

    /// <summary>Читает пояс из конфигурации; неизвестный идентификатор — явная ошибка старта (не тихий UTC).</summary>
    public DocFlowClock(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var timeZoneId = configuration["DocFlow:TimeZone"] ?? "Asia/Bishkek";
        _timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
    }

    /// <inheritdoc />
    public DateTime UtcNow => DateTime.UtcNow;

    /// <inheritdoc />
    public DateOnly Today =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(DateTimeOffset.UtcNow, _timeZone).DateTime);
}
