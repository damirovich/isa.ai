using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.DocFlow.Application;

/// <summary>
/// Регистрация прикладных сервисов модуля документооборота (сценарии, валидаторы).
/// Обработчики Mediator регистрирует source-генератор в хосте <c>Web</c>.
/// </summary>
public static class DocFlowApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует сценарии и валидаторы документооборота.
    /// СКЕЛЕТ (этап 0 задачи Э4-35): регистрировать пока нечего — сервисы появятся вместе с переносом
    /// сценариев из СКИД (этап 3). Метод объявлен сразу, чтобы шов композиции был собран и проверен.
    /// </summary>
    public static IServiceCollection AddDocFlowApplication(this IServiceCollection services) => services;
}
