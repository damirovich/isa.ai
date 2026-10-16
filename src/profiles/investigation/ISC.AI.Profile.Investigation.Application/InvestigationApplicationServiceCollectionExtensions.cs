using FluentValidation;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

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

        // Окно редактирования сводок и срок разрешения на правку (ТФ-ДДЛ-05, ADR-0031): по умолчанию 48 и 24 часа;
        // неположительное или нечисловое значение в конфигурации — умолчание, а не «окно 0 часов».
        services.AddSingleton(new CaseReportOptions(
            PositiveOr(configuration[CaseReportOptions.EditWindowKey], 48),
            PositiveOr(configuration[CaseReportOptions.PermitHoursKey], 24)));

        // Документы архива при пакетном импорте (ADR-0042): в документооборот группы «Хранение» с привязкой к делу.
        // Заменяет поставку пакета «Медиа» по умолчанию («документы не принимаются»): регистрация пакета — TryAdd.
        services.AddScoped<ISC.AI.Modules.Media.Domain.Services.IArchiveDocumentSink, Features.CaseDocuments.ArchiveDocumentSink>();

        // Перенос баз Access (ADR-0043): файл базы в хранилище со сверкой SHA-256 и сам перенос — теми же
        // хранилищами, что ручной ввод (чтение базы драйвером ACE регистрирует слой данных).
        services.AddScoped<Features.AccessImport.AccessImportFiles>();
        services.AddScoped<Features.AccessImport.AccessImporter>();

        // Ссылки в ответах чата по материалам дел (ADR-0051, ТБ-040): профиль поставляет извлечение номеров, статей и
        // кодексов вместо заглушки ядра «ссылок нет»; саму проверку по поданным фрагментам ведёт ядровой валидатор.
        services.Replace(ServiceDescriptor.Singleton<ICitationExtractor, Features.CaseChat.CaseCitationExtractor>());
        services.Replace(ServiceDescriptor.Singleton<ICitationNormalizer, Features.CaseChat.CaseCitationNormalizer>());

        // Чат — пакет «Чат» (ADR-0052); профиль закрывает его порт: области — дела под решёткой, материалы (ADR-0051) —
        // документы дел, расшифровки записей, сводки и справки, сведения дел; право — «Чат по материалам дел».
        services.AddScoped<Features.CaseChat.ChatMaterialResolver>();
        services.AddScoped<ISC.AI.Modules.Chat.Domain.Services.IChatProfile, Features.CaseChat.InvestigationChatProfile>();

        services.AddValidatorsFromAssembly(typeof(InvestigationApplicationServiceCollectionExtensions).Assembly);
        return services;
    }

    private static int PositiveOr(string? value, int fallback) =>
        int.TryParse(value, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var hours) && hours > 0
            ? hours
            : fallback;
}
