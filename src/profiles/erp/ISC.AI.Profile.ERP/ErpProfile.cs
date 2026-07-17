using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Modules;
using ISC.AI.Abstractions.Profiles;
using ISC.AI.Profile.ERP.Application;
using ISC.AI.Profile.ERP.Data;
using ISC.AI.Profile.ERP.UI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Profile.ERP;

/// <summary>
/// Манифест профиля «АИС ЕРП» (единая регистрация преступлений) — КАРКАС на том же нейтральном ядре,
/// что и профиль «Инспектор» (ТС-004, ТС-006, ADR-0002).
/// </summary>
/// <remarks>
/// Профиль ПОДКЛЮЧАЕТСЯ ХОСТОМ и является сменной предметной частью: ядро о нём не знает. Сейчас —
/// заготовка с одним модулем-заглушкой; состав (регистрация преступлений, распознавание рукописных
/// документов, связывание эпизодов, отчёты) определится по деталям проекта и контракту API АИС ЕРП.
/// В хост пока НЕ подключён: действует инвариант «ровно один профиль в хосте» (§5.1.1.9) — «Инспектор».
/// Интеграция с внешней АИС ЕРП — только через её API, изолированно коннектором-адаптером (антикоррупционный
/// слой), чтобы чужая модель не протекала в ядро и профиль.
/// </remarks>
public sealed class ErpProfile : IProfile
{
    /// <inheritdoc />
    public string Id => "erp";

    /// <inheritdoc />
    public string DisplayName => "АИС ЕРП";

    /// <inheritdoc />
    public IReadOnlyList<IModule> Modules { get; } =
    [
        new ModuleDescriptor(
            Id: "erp-home",
            Route: "/erp",
            MenuTitle: "ЕРП",
            MenuIcon: null,
            ComponentType: typeof(ErpHome),
            RequiredPolicy: "erp.read"),
    ];

    /// <inheritdoc />
    public IReadOnlyList<IModelContributor> ModelContributors { get; } = [];

    /// <inheritdoc />
    public void RegisterServices(IServiceCollection services, IConfiguration configuration) =>
        services.AddErpApplication();

    /// <inheritdoc />
    public void RegisterDataContexts(IServiceCollection services, IConfiguration configuration) =>
        services.AddErpPersistence(configuration);
}
