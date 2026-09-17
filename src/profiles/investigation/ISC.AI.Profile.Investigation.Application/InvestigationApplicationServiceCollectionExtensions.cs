using FluentValidation;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Application;

/// <summary>Регистрация сценариев профиля «Следствие» в контейнере хоста.</summary>
public static class InvestigationApplicationServiceCollectionExtensions
{
    /// <summary>Регистрирует валидаторы сценариев. Обработчики Mediator регистрирует source-генератор хоста.</summary>
    public static IServiceCollection AddInvestigationApplication(this IServiceCollection services)
    {
        services.AddValidatorsFromAssembly(typeof(InvestigationApplicationServiceCollectionExtensions).Assembly);
        return services;
    }
}
