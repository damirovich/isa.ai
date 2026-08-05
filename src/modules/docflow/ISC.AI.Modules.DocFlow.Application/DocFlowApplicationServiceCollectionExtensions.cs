using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application;

/// <summary>
/// Регистрация прикладных сервисов модуля документооборота (сценарии, валидаторы).
/// Обработчики Mediator регистрирует source-генератор в хосте <c>Web</c>.
/// </summary>
public static class DocFlowApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы сценариев модуля (сквозной <c>ValidationBehavior</c> хоста берёт их из DI).
    /// </summary>
    public static IServiceCollection AddDocFlowApplication(this IServiceCollection services) =>
        services.AddValidatorsFromAssembly(typeof(DocFlowApplicationServiceCollectionExtensions).Assembly);
}
