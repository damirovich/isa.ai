using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Components.Server.Circuits;
using Microsoft.AspNetCore.Http;

namespace ISC.AI.Web.Security;

/// <summary>
/// Соединение Blazor и сеанс входа (ТФ-АДМ-09, ADR-0060): регистрирует соединение в реестре живых сеансов, отмечает
/// активность по действиям пользователя и даёт реестру способ немедленно перевести соединение в «не вошёл». Заодно
/// захватывает IP-адрес клиента соединения для журнала аудита (ТБ-080, ADR-0066) — при установлении и переподключении.
/// </summary>
/// <remarks>
/// После загрузки страницы пользователь работает через соединение, а не HTTP-запросами: без этого обработчика
/// «последняя активность» стояла бы на месте, а завершение сеанса доходило бы до открытой вкладки лишь при
/// ревалидации. Перевод в «не вошёл» — <see cref="StampRevalidatingAuthenticationStateProvider.SignOutNow"/>: страница
/// уходит на форму входа, а перезагрузку отклоняет проверка cookie (<see cref="CookiePrincipalValidator"/>).
/// </remarks>
public sealed class SessionCircuitHandler(
    UserSessionTracker tracker,
    AuthenticationStateProvider authenticationStateProvider,
    IHttpContextAccessor httpContextAccessor,
    CircuitClientAddress clientAddress)
    : CircuitHandler
{
    private Guid? _sessionId;

    /// <inheritdoc />
    public override async Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        CaptureClientAddress();
        var state = await authenticationStateProvider.GetAuthenticationStateAsync();
        _sessionId = UserSessionTracker.SessionIdOf(state.User);
        if (_sessionId is { } sessionId)
        {
            tracker.RegisterCircuit(this, sessionId, SignOut);
        }
    }

    /// <inheritdoc />
    public override Task OnConnectionUpAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        // Переподключение могло прийти уже с другого адреса (сменилась сеть) — журнал пишет текущий.
        CaptureClientAddress();
        tracker.SetCircuitConnected(this, connected: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnConnectionDownAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.SetCircuitConnected(this, connected: false);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Task OnCircuitClosedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        tracker.UnregisterCircuit(this);
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public override Func<CircuitInboundActivityContext, Task> CreateInboundActivityHandler(
        Func<CircuitInboundActivityContext, Task> next) =>
        async context =>
        {
            // Действие пользователя в соединении — активность сеанса (в базе — не чаще раза в минуту).
            if (_sessionId is { } sessionId)
            {
                await tracker.NoteActivityAsync(sessionId, CancellationToken.None);
            }

            await next(context);
        };

    // Адрес клиента соединения для журнала аудита (ТБ-080, ADR-0066). Соединение и переподключение устанавливаются
    // вызовом концентратора внутри HTTP-запроса самого соединения (WebSocket), и его контекст здесь доступен; за
    // доверенным прокси адрес уже взят из X-Forwarded-For. Транспорт без постоянного запроса контекста может не дать —
    // тогда журнал возьмёт адрес входа в сеанс (HttpSubjectProvider).
    private void CaptureClientAddress() =>
        clientAddress.Capture(ClientAddresses.Format(httpContextAccessor.HttpContext?.Connection.RemoteIpAddress));

    private void SignOut()
    {
        if (authenticationStateProvider is StampRevalidatingAuthenticationStateProvider provider)
        {
            provider.SignOutNow();
        }
    }
}
