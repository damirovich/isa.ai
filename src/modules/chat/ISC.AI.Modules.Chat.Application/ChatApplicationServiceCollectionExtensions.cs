using ISC.AI.Modules.Chat.Domain.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ISC.AI.Modules.Chat.Application;

/// <summary>
/// Регистрация прикладных сервисов пакета «Чат» (ADR-0052). Обработчики Mediator регистрирует source-генератор в хосте
/// <c>Web</c>.
/// </summary>
public static class ChatApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует настройки чата для экранов пакета — их отдаёт порт профиля.
    /// </summary>
    /// <remarks>
    /// Порт <see cref="IChatProfile"/> здесь НЕ регистрируется намеренно: его поставляет профиль. Без него контейнер не
    /// соберёт обработчик и приложение не запустится — осознанный fail-closed (ТС-013): чат без правила области и
    /// решётки опаснее остановки. Экраны берут из контейнера только <see cref="ChatSettings"/> — к данным порта у них
    /// пути нет, всё остальное идёт через сценарии.
    /// </remarks>
    public static IServiceCollection AddChatApplication(this IServiceCollection services)
    {
        services.TryAddScoped(provider => provider.GetRequiredService<IChatProfile>().Settings);
        return services;
    }
}
