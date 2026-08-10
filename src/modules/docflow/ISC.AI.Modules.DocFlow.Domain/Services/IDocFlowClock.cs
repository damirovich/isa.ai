namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Часы модуля: «сегодня» в часовом поясе эксплуатанта (сроки в ТЗ СКИД — календарные даты §4.2/§4.6,
/// а сервер хранит UTC; на границе суток они расходятся). Пояс — конфиг <c>DocFlow:TimeZone</c>,
/// по умолчанию <c>Asia/Bishkek</c> (как в СКИД).
/// </summary>
public interface IDocFlowClock
{
    /// <summary>Текущий момент (UTC).</summary>
    DateTime UtcNow { get; }

    /// <summary>Сегодняшняя дата в поясе эксплуатанта — база проверки сроков и дат регистрации.</summary>
    DateOnly Today { get; }
}
