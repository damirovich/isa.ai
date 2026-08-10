using ISC.AI.Abstractions.Security;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Web.Security;

/// <summary>
/// Учётная запись по умолчанию (Э4-35 §6.5): создаётся при старте, если в системе НЕТ НИ ОДНОГО
/// пользователя. Логин и пароль — из секции <c>Auth:DefaultAccount</c>.
/// </summary>
/// <remarks>
/// РЕШЕНИЕ ЗАКАЗЧИКА (2026-08-07): развёртывание не должно требовать отдельной команды. Осознанная
/// плата — пароль лежит в конфигурации, то есть известен всем, у кого есть доступ к репозиторию или
/// к копиям конфигов. Это ограничено ДВУМЯ мерами, и обе обязательны:
///
/// 1. Учётка создаётся с признаком ВРЕМЕННОГО пароля. До его смены оболочка не пускает никуда,
///    кроме страницы смены (<c>RequirePasswordChange</c>), а смена меняет штамп безопасности —
///    пароль из конфигурации перестаёт действовать навсегда. Без этой меры засев был бы постоянной
///    открытой дверью, а не разовым входом.
/// 2. Засев срабатывает ТОЛЬКО на ПОЛНОСТЬЮ ПУСТОМ реестре. Условие «нет администратора» здесь не
///    годится: удалили или разжаловали всех — и при следующем перезапуске молча появился бы вход
///    с паролем из конфига, о котором никто не помнит.
///
/// При развёртывании на боевом контуре пароль в конфигурации обязан быть заменён (или задан
/// переменной окружения <c>Auth__DefaultAccount__Password</c>, чтобы не попадать в репозиторий).
/// </remarks>
public sealed class DefaultAccountSeeder(
    IServiceScopeFactory scopeFactory,
    IConfiguration configuration,
    ILogger<DefaultAccountSeeder> logger) : IHostedService
{
    /// <inheritdoc />
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var login = configuration["Auth:DefaultAccount:UserName"];
        var password = configuration["Auth:DefaultAccount:Password"];

        // Пустой пароль — засев отключён. Это штатный способ его выключить на контуре, где учётки
        // заводят командой: молча создавать вход с пустым паролем недопустимо.
        if (string.IsNullOrWhiteSpace(login) || string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var accounts = scope.ServiceProvider.GetRequiredService<IUserAccountStore>();

            var existing = await accounts.ListAsync(cancellationToken);
            if (existing.Count > 0)
            {
                // В системе уже есть пользователи — засев не нужен и опасен (см. remarks, п. 2).
                return;
            }

            var userId = await accounts.CreateAsync(login, "Администратор", password, cancellationToken);
            if (userId is null)
            {
                return;
            }

            // В журнал приложения идёт только ЛОГИН: пароль не логируется ни в каком виде (ТБ-043).
            DefaultAccountLog.Created(logger, login);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Сбой засева не должен мешать старту: система поднимется, а учётку заведут командой
            // create-account. Молчать при этом нельзя — иначе причина «не могу войти» останется тайной.
            DefaultAccountLog.Failed(logger, ex);
        }
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Строго-типизированные сообщения засева (LoggerMessage — CA1848).</summary>
internal static partial class DefaultAccountLog
{
    [LoggerMessage(Level = LogLevel.Warning,
        Message = "Создана учётная запись по умолчанию «{Login}» с ВРЕМЕННЫМ паролем из конфигурации. "
            + "До смены пароля работа в системе заблокирована; пароль из конфигурации перестанет "
            + "действовать сразу после смены.")]
    public static partial void Created(ILogger logger, string login);

    [LoggerMessage(Level = LogLevel.Error,
        Message = "Учётная запись по умолчанию не создана — заведите её командой: "
            + "dotnet run --project src/core/ISC.AI.Web -- create-account <логин>")]
    public static partial void Failed(ILogger logger, Exception exception);
}
