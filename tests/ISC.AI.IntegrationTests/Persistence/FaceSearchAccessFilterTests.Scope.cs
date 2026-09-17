using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Data;
using ISC.AI.Modules.Media.Domain.Model;
using Microsoft.EntityFrameworkCore;
using Shouldly;

namespace ISC.AI.IntegrationTests.Persistence;

// GATE-4, область поиска (ТБ-071, ТФ-ПЛ-05): AssetIds сужает выдачу ПОСЛЕ решётки, а не вместо неё.
public sealed partial class FaceSearchAccessFilterTests
{
    [Fact(DisplayName = "GATE-4: область поиска (AssetIds) сужает выдачу ПОСЛЕ решётки; пустая область — пусто, не «все»")]
    public async Task Asset_scope_narrows_results_after_access_filter()
    {
        var factory = new MediaContextFactory(_postgres.GetConnectionString());
        int accessibleInScope, accessibleOutOfScope, restrictedInScope;
        await using (var db = factory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
            // Все векторы одинаковы: по расстоянию кандидаты неразличимы, различает только решётка и область.
            accessibleInScope = await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: true, acceptable: true);
            accessibleOutOfScope = await AddAssetWithTemplateAsync(db, classification: 1, divisionId: 7, isCurrent: true, acceptable: true);
            restrictedInScope = await AddAssetWithTemplateAsync(db, classification: 2, divisionId: 7, isCurrent: true, acceptable: true);
        }

        var search = new PgVectorFaceSearch(factory, new AllowAllAccessPolicy());
        var access = new AccessContext("u1", MaxClassification: 1, AllowedDivisions: [7]);

        // Без области — все доступные по решётке (служебный сценарий).
        var unscoped = await search.SearchAsync(new FaceSearchQuery(Probe(), TopK: 50), access);
        unscoped.Select(c => c.AssetId).ShouldBe([accessibleInScope, accessibleOutOfScope], ignoreOrder: true);

        // Область из двух носителей: доступный вне области НЕ выдаётся; недоступный В области — тоже
        // (область не расширяет допуск: решётка стоит первой).
        var scoped = await search.SearchAsync(
            new FaceSearchQuery(Probe(), TopK: 50, AssetIds: [accessibleInScope, restrictedInScope]), access);
        scoped.ShouldHaveSingleItem().AssetId.ShouldBe(accessibleInScope);

        // Область только из недоступного носителя — пусто, неотличимо от «лица нет» (ТБ-020/021).
        (await search.SearchAsync(new FaceSearchQuery(Probe(), TopK: 50, AssetIds: [restrictedInScope]), access))
            .ShouldBeEmpty();

        // Пустая область — пустая выдача, а не «все носители» (у субъекта нет дел с носителями).
        (await search.SearchAsync(new FaceSearchQuery(Probe(), TopK: 50, AssetIds: Array.Empty<int>()), access))
            .ShouldBeEmpty();
    }
}
