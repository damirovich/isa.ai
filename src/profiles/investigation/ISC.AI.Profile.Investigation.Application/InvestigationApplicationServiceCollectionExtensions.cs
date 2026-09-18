using FluentValidation;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.Investigation.Application;

/// <summary>Регистрация сценариев профиля «Следствие» в контейнере хоста.</summary>
public static class InvestigationApplicationServiceCollectionExtensions
{
    /// <summary>
    /// Регистрирует валидаторы сценариев и правила хранения биометрии (ТБ-074/078, ADR-0024).
    /// Обработчики Mediator регистрирует source-генератор хоста.
    /// </summary>
    public static IServiceCollection AddInvestigationApplication(
        this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        // Правила хранения читаются ОДИН раз при старте: это решение эксплуатанта из ведомственного акта,
        // а не параметр операции — менять его на ходу означало бы менять регламент без документа.
        // Нечитаемое или отсутствующее значение = «хранить»: направление отказа выбрано в пользу
        // сохранности материалов дела — потерянные шаблоны не восстановить, а лишние можно удалить позже.
        var purgeOnClosure =
            bool.TryParse(configuration[InvestigationRetentionOptions.PurgeOnClosureKey], out var parsed) && parsed;

        services.AddSingleton(new InvestigationRetentionOptions(purgeOnClosure));
        services.AddValidatorsFromAssembly(typeof(InvestigationApplicationServiceCollectionExtensions).Assembly);
        return services;
    }
}
