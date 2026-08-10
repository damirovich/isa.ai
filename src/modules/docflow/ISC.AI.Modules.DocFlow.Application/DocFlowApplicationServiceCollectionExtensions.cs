using FluentValidation;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application;

/// <summary>
/// Регистрация прикладных сервисов модуля документооборота (сценарии, валидаторы).
/// Обработчики Mediator регистрирует source-генератор в хосте <c>Web</c>.
/// </summary>
public static class DocFlowApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы сценариев модуля (сквозной <c>ValidationBehavior</c> хоста берёт их из DI)
    /// и составитель уведомлений (разд. 5): «кому и какой текст» — прикладное правило, не хранилище.
    /// </summary>
    public static IServiceCollection AddDocFlowApplication(this IServiceCollection services) =>
        services
            .AddScoped<DocFlowEventNotifier>()
            .AddValidatorsFromAssembly(typeof(DocFlowApplicationServiceCollectionExtensions).Assembly);
}
