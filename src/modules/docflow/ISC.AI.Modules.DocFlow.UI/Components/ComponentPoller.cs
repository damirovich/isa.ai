namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>
/// Фоновый цикл опроса на время жизни компонента. Вынесен из <c>NotificationBell</c> и
/// <c>NotificationsPanel</c>, где жил двумя дословными копиями, — это самый аварийно-опасный код
/// компонентов (необработанное исключение фоновой задачи роняет весь circuit), и существовать он
/// должен в одном экземпляре.
/// </summary>
/// <remarks>
/// Зашитые здесь уроки (см. историю Э4-35, дашборд/уведомления):
/// <list type="bullet">
/// <item><see cref="Start"/> терпит повторный вызов — предрендер выполняет
/// <c>OnInitializedAsync</c> ДВАЖДЫ, и второй запуск оставил бы цикл-сироту, который некому
/// остановить;</item>
/// <item><see cref="OperationCanceledException"/> — штатное завершение при уничтожении
/// компонента;</item>
/// <item><see cref="ObjectDisposedException"/> ГЛОТАЕТСЯ намеренно: гонка ухода со страницы —
/// тик уже случился, а рендерер (или таймер) уже уничтожен; <c>InvokeAsync</c> на уничтоженном
/// рендерере бросает именно это, а необработанное исключение фоновой задачи роняет circuit —
/// пользователь видел бы «произошла необработанная ошибка», просто уйдя с экрана.</item>
/// </list>
/// Владелец передаёт в <paramref name="tick"/> ПОЛНУЮ операцию тика, включая маршалинг в поток
/// рендера (<c>InvokeAsync(...)</c> + <c>StateHasChanged</c>): опросчик не знает о компоненте
/// ничего, кроме того, что его надо периодически будить.
/// </remarks>
public sealed class ComponentPoller(TimeSpan interval) : IAsyncDisposable
{
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _cts;

    /// <summary>Запускает цикл; повторный вызов (предрендер) игнорируется.</summary>
    public void Start(Func<Task> tick)
    {
        if (_cts is not null)
        {
            return;
        }

        _cts = new CancellationTokenSource();
        _timer = new PeriodicTimer(interval);
        var token = _cts.Token;
        var timer = _timer;

        // Цикл НЕ ждётся здесь намеренно: он живёт столько же, сколько компонент-владелец.
        _ = Task.Run(async () =>
        {
            try
            {
                while (await timer.WaitForNextTickAsync(token))
                {
                    await tick();
                }
            }
            catch (OperationCanceledException)
            {
                // Компонент уничтожен — штатное завершение цикла.
            }
            catch (ObjectDisposedException)
            {
                // Гонка ухода со страницы — см. remarks класса.
            }
        }, token);
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        if (_cts is not null)
        {
            await _cts.CancelAsync();
            _cts.Dispose();
            _cts = null;
        }

        _timer?.Dispose();
        _timer = null;
    }
}
