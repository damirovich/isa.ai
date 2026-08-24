using System.Threading.Tasks;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;
using Xunit;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Реестр методик (§5.2.9, Ц-03) на настоящем PostgreSQL. Требуется Docker.
/// </summary>
/// <remarks>
/// Главные инварианты: РЕЖИМ — методика с грифом выше допуска не выдаётся и не правится, для
/// субъекта она неотличима от несуществующей (ТБ-020-стиль); правка текста возвращает утверждённую
/// в черновики (утверждение относится к редакции); удаляется только черновик.
/// </remarks>
public sealed class MethodRegistryTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Реестр не отдаёт и не правит методики выше допуска субъекта")]
    public async Task Registry_never_exceeds_subject_clearance()
    {
        var store = await BuildAsync();

        var openId = await store.SaveAsync(Draft("Чек-лист", "Делопроизводство ГИ", classification: 0));
        var secretId = await store.SaveAsync(Draft("Чек-лист", "Агентурная работа ТУ", classification: 5));

        // Допуск 0: видна только открытая; секретная неотличима от несуществующей.
        var limited = await store.ListAsync(new MethodListFilter(), maxClassification: 0);
        limited.TotalCount.ShouldBe(1);
        limited.Rows.ShouldHaveSingleItem().Id.ShouldBe(openId);

        (await store.GetAsync(secretId, maxClassification: 0)).ShouldBeNull();
        (await store.UpdateBodyAsync(secretId, "патч", maxClassification: 0)).ShouldBe(MethodWriteResult.NotFound);
        (await store.SetStatusAsync(secretId, MethodDocumentStatus.Approved, null, maxClassification: 0))
            .ShouldBe(MethodWriteResult.NotFound);
        (await store.DeleteAsync(secretId, maxClassification: 0)).ShouldBe(MethodWriteResult.NotFound);

        // Полный допуск — обе; поиск по объекту сужает.
        (await store.ListAsync(new MethodListFilter(), maxClassification: 9)).TotalCount.ShouldBe(2);
        var search = await store.ListAsync(new MethodListFilter(Search: "агентур"), maxClassification: 9);
        search.Rows.ShouldHaveSingleItem().Id.ShouldBe(secretId);
    }

    [Fact(DisplayName = "Правка возвращает утверждённую в черновики; удаляется только черновик")]
    public async Task Editing_demotes_approved_and_only_drafts_are_deletable()
    {
        var store = await BuildAsync();
        var approverId = await SeedUserAsync("Руководитель Р.");

        var methodId = await store.SaveAsync(Draft("Программа проверки", "Нарынская инспекция", 0));

        (await store.SetStatusAsync(methodId, MethodDocumentStatus.Approved, approverId, 9))
            .ShouldBe(MethodWriteResult.Ok);
        var approved = await store.GetAsync(methodId, 9);
        approved!.Status.ShouldBe(MethodDocumentStatus.Approved);
        approved.ApprovedByName.ShouldBe("Руководитель Р.");

        // Утверждённая не удаляется.
        (await store.DeleteAsync(methodId, 9)).ShouldBe(MethodWriteResult.NotDraft);

        // Правка текста возвращает в черновики и снимает отметку утверждения.
        (await store.UpdateBodyAsync(methodId, "выверенная редакция", 9)).ShouldBe(MethodWriteResult.Ok);
        var demoted = await store.GetAsync(methodId, 9);
        demoted!.Status.ShouldBe(MethodDocumentStatus.Draft);
        demoted.ApprovedByName.ShouldBeNull();
        demoted.Body.ShouldBe("выверенная редакция");

        // Черновик удаляется; повторное удаление — не найдено.
        (await store.DeleteAsync(methodId, 9)).ShouldBe(MethodWriteResult.Ok);
        (await store.DeleteAsync(methodId, 9)).ShouldBe(MethodWriteResult.NotFound);
    }

    private static MethodDocumentDraft Draft(string kind, string scope, short classification) => new(
        kind, "Целевая", scope, "Текст методики.", CitationsJson: null,
        AllCitationsConfirmed: true, Classification: classification, CreatedByUserId: null);

    private async Task<MethodRegistryStore> BuildAsync()
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using (var db = core.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var inspector = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = inspector.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        return new MethodRegistryStore(inspector, core);
    }

    private async Task<int> SeedUserAsync(string displayName)
    {
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await using var db = core.CreateDbContext();
        var user = new AppUserEntity { UserName = "approver", DisplayName = displayName };
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }
}
