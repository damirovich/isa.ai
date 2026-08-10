namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Итог одной проверки сроков.</summary>
/// <param name="MarkedOverdue">Сколько назначений система перевела в «Просрочено» (§4.2).</param>
/// <param name="DeadlineNotices">Сколько назначений попало в окно уведомлений о сроке (разд. 5).</param>
public sealed record DeadlineCheckResult(int MarkedOverdue, int DeadlineNotices);

/// <summary>
/// Проверка сроков: уведомления о приближении и перевод истёкших в «Просрочено» (§4.2/разд. 5).
/// </summary>
/// <remarks>
/// Вынесена ОТДЕЛЬНО от фоновой задачи намеренно. Раньше вся логика жила внутри
/// <c>BackgroundService</c>, и позвать её иначе, чем дождавшись часового тика, было нельзя: при
/// развёртывании и при разборе жалобы «почему не пришло уведомление» оставалось только ждать.
/// Теперь задача — тонкий цикл поверх этого сервиса, а администратор может запустить проверку руками.
/// Повторный запуск БЕЗОПАСЕН: перевод в «Просрочено» идемпотентен (уже просроченные не трогаются),
/// а уведомления отсекает дедупликация по (назначение, вид, значение срока).
/// </remarks>
public interface IDeadlineChecker
{
    /// <summary>Выполняет одну проверку.</summary>
    Task<DeadlineCheckResult> RunOnceAsync(CancellationToken cancellationToken = default);
}
