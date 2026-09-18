namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Фоновый цикл опроса на время жизни компонента (автообновление статусов индексации носителей).
/// Логика повторяет одноимённый класс пакета документооборота: пакеты друг на друга не ссылаются
/// (ADR-0017), а этот код — самый аварийно-опасный в компонентах (необработанное исключение фоновой
/// задачи роняет весь circuit), поэтому он должен быть коротким и одинаковым.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><see cref="Start"/> терпит повторный вызов — предрендер выполняет <c>OnInitializedAsync</c>
/// ДВАЖДЫ, и второй запуск оставил бы цикл-сироту;</item>
/// <item><see cref="OperationCanceledException"/> — штатное завершение при уничтожении компонента;</item>
/// <item><see cref="ObjectDisposedException"/> глотается намеренно: гонка ухода со страницы — тик уже
/// случился, а рендерер уничтожен; <c>InvokeAsync</c> на уничтоженном рендерере бросает именно это.</item>
/// </list>
/// Владелец передаёт в <paramref name="tick"/> ПОЛНУЮ операцию тика, включая маршалинг в поток рендера.
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
