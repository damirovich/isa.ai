using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Системные настройки модуля (§9 ТЗ СКИД) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Смысл переноса настройки из файла в базу — менять её без перезапуска. Поэтому проверяется не
/// только запись, но и то, что кеш сбрасывается: иначе новое значение начало бы действовать лишь
/// после рестарта, то есть ровно так же, как раньше.
/// </remarks>
public sealed class SystemSettingsTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Без записи в БД берётся значение конфигурации, иначе — дефолт СКИД")]
    public async Task Falls_back_to_configuration_then_default()
    {
        var cache = new DocFlowSettingsCache();
        var factory = await MigratedFactoryAsync();

        var withoutConfig = new SystemSettingsStore(factory, cache, Config(null));
        (await withoutConfig.GetAsync()).NotificationHorizonDays
            .ShouldBe(SystemSettingsStore.DefaultNotificationHorizonDays);

        // Обратная совместимость: развёртывания, где горизонт прописан в appsettings, обязаны
        // продолжать работать с прежним значением, пока настройку не поменяли в интерфейсе.
        var withConfig = new SystemSettingsStore(factory, new DocFlowSettingsCache(), Config("14"));
        (await withConfig.GetAsync()).NotificationHorizonDays.ShouldBe(14);
    }

    [Fact(DisplayName = "Сохранённое значение перекрывает конфигурацию и действует сразу (кеш сброшен)")]
    public async Task Stored_value_wins_and_cache_is_invalidated()
    {
        var cache = new DocFlowSettingsCache();
        var factory = await MigratedFactoryAsync();
        var store = new SystemSettingsStore(factory, cache, Config("14"));

        // Прогреваем кеш прежним значением — иначе тест не отличил бы сброс от «ещё не читали».
        (await store.GetAsync()).NotificationHorizonDays.ShouldBe(14);

        (await store.SetNotificationHorizonAsync(3, changedByUserId: 42)).ShouldBeTrue();

        (await store.GetAsync()).NotificationHorizonDays.ShouldBe(3);

        // И для нового читателя (другой запрос — другой scope) значение то же.
        var another = new SystemSettingsStore(factory, cache, Config("14"));
        (await another.GetAsync()).NotificationHorizonDays.ShouldBe(3);
    }

    /// <summary>
    /// Границы проверяются в ХРАНИЛИЩЕ, а не только в форме: горизонт задаёт окно отбора фоновой
    /// проверки сроков, и значение вроде 3650 превратило бы тик в рассылку по всему корпусу.
    /// </summary>
    [Fact(DisplayName = "Горизонт вне границ 1..30 отклоняется и не портит сохранённое значение")]
    public async Task Out_of_range_is_rejected()
    {
        var cache = new DocFlowSettingsCache();
        var factory = await MigratedFactoryAsync();
        var store = new SystemSettingsStore(factory, cache, Config(null));

        (await store.SetNotificationHorizonAsync(10, 42)).ShouldBeTrue();

        (await store.SetNotificationHorizonAsync(0, 42)).ShouldBeFalse();
        (await store.SetNotificationHorizonAsync(31, 42)).ShouldBeFalse();

        (await store.GetAsync()).NotificationHorizonDays.ShouldBe(10);
    }

    [Fact(DisplayName = "Мусор в строке настройки не роняет фоновую задачу")]
    public async Task Garbage_value_falls_back()
    {
        var factory = await MigratedFactoryAsync();

        // Правка руками в БД в обход приложения — значение обязано быть приравнено к «не задано».
        await using (var db = factory.CreateDbContext())
        {
            db.SystemSettings.Add(new ISC.AI.Modules.DocFlow.Domain.Entities.SystemSetting
            {
                Key = DocFlowSettings.NotificationHorizonKey,
                Value = "как-нибудь",
            });
            await db.SaveChangesAsync();
        }

        var store = new SystemSettingsStore(factory, new DocFlowSettingsCache(), Config(null));
        (await store.GetAsync()).NotificationHorizonDays
            .ShouldBe(SystemSettingsStore.DefaultNotificationHorizonDays);
    }

    private static IConfiguration Config(string? horizon)
    {
        var values = new Dictionary<string, string?>();
        if (horizon is not null)
        {
            values["DocFlow:NotificationHorizonDays"] = horizon;
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private async Task<DocFlowContextFactory> MigratedFactoryAsync()
    {
        var factory = new DocFlowContextFactory(_postgres.GetConnectionString());
        await using var db = factory.CreateDbContext();
        await db.Database.MigrateAsync();
        return factory;
    }
}
