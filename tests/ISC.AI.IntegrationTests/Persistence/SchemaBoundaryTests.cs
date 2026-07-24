using ISC.AI.Persistence;
using ISC.AI.Profile.Inspector.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Shouldly;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Инвариант границы схем (ТО-инф-06, ADR-0003): между схемами <c>core</c> и <c>inspector</c> НЕТ внешних
/// ключей — связи только по значению идентификатора. Тест строит модели EF ОБОИХ контекстов (без подключения
/// к БД — Docker не нужен) и проверяет, что ни один контекст не маппит чужие сущности и ни один FK не
/// пересекает границу схем. Инвариант держится сборкой, а не ревью миграций (Э4-13).
/// </summary>
public sealed class SchemaBoundaryTests
{
    [Fact(DisplayName = "ТО-инф-06/ADR-0003: между схемами core и inspector нет FK (только ссылки по значению)")]
    public void No_foreign_keys_cross_the_core_inspector_boundary()
    {
        using var core = BuildContext<CoreDbContext>(options => new CoreDbContext(options));
        using var inspector = BuildContext<InspectorDbContext>(options => new InspectorDbContext(options));

        // Контекст ядра не маппит сущности профиля, контекст профиля не маппит сущности ядрового Persistence —
        // раз чужих сущностей в модели нет, FK через границу схем невозможен в принципе.
        AssertNoEntitiesFromAssembly(core.Model, "ISC.AI.Profile.", nameof(CoreDbContext));
        AssertNoEntitiesFromAssembly(inspector.Model, "ISC.AI.Persistence", nameof(InspectorDbContext));

        // Прямая проверка: каждый FK остаётся внутри своей схемы.
        AssertForeignKeysStayInSchema(core.Model, CoreDbContext.Schema);
        AssertForeignKeysStayInSchema(inspector.Model, InspectorDbContext.Schema);
    }

    private static void AssertNoEntitiesFromAssembly(IModel model, string assemblyPrefix, string contextName)
    {
        var foreign = model.GetEntityTypes()
            .Where(e => e.ClrType.Assembly.GetName().Name?.StartsWith(assemblyPrefix, StringComparison.Ordinal) == true)
            .Select(e => e.ClrType.FullName)
            .ToArray();

        foreign.ShouldBeEmpty(
            $"{contextName} маппит чужие сущности ({assemblyPrefix}*) — риск FK через границу схем: {string.Join(", ", foreign)}");
    }

    private static void AssertForeignKeysStayInSchema(IModel model, string schema)
    {
        foreach (var entity in model.GetEntityTypes())
        {
            foreach (var foreignKey in entity.GetForeignKeys())
            {
                var principalSchema = foreignKey.PrincipalEntityType.GetSchema() ?? schema;
                var dependentSchema = foreignKey.DeclaringEntityType.GetSchema() ?? schema;

                principalSchema.ShouldBe(dependentSchema,
                    $"FK у «{entity.ClrType.Name}» пересекает границу схем: {dependentSchema} → {principalSchema}.");
            }
        }
    }

    // Модель строится офлайн: обращение к .Model запускает OnModelCreating без подключения к БД (без Docker).
    private static TContext BuildContext<TContext>(Func<DbContextOptions<TContext>, TContext> create)
        where TContext : DbContext
    {
        var options = new DbContextOptionsBuilder<TContext>()
            .UseNpgsql("Host=offline;Database=model-only", npgsql => npgsql.UseVector())
            .UseSnakeCaseNamingConvention()
            .Options;
        return create(options);
    }
}
