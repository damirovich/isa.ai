using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Документы дела (ТФ-ДЕЛ-02) на настоящем Postgres: прикрепление, повтор, открепление; дело чужого следователя
/// неотличимо от отсутствующего; уничтожение дела снимает привязки каскадом.
/// </summary>
public sealed class InvestigationCaseDocumentsTests : IAsyncLifetime
{
    private const int Owner = 10;
    private const int Stranger = 11;

    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "Документы дела: прикрепить, повтор — AlreadyLinked, открепить; чужому — NotFound; уничтожение дела уносит привязки")]
    public async Task Links_follow_case_access()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (Owner, InvestigationRole.Investigator), (Stranger, InvestigationRole.Investigator));
        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var store = new CaseDocumentStore(factory, new InvestigationAccessPolicy(factory), new UserRoleStore(core, factory));
        var owner = InvestigationTestKit.Access(Owner, 3, 5);
        var stranger = InvestigationTestKit.Access(Stranger, 3, 5);
        var caseId = (await cases.CreateAsync(InvestigationTestKit.Draft("А-1/26", 5, 2, Owner), owner)).CaseId;

        (await store.AttachAsync(caseId, 101, owner)).ShouldBe(CaseDocumentWriteResult.Ok);
        (await store.AttachAsync(caseId, 102, owner)).ShouldBe(CaseDocumentWriteResult.Ok);
        (await store.AttachAsync(caseId, 101, owner)).ShouldBe(CaseDocumentWriteResult.AlreadyLinked);

        var links = (await store.ListAsync(caseId, owner)).ShouldNotBeNull();
        links.Select(l => l.DocumentId).ShouldBe([102, 101]); // новые первыми
        links.ShouldAllBe(l => l.LinkedByUserId == Owner);

        (await store.ListAsync(caseId, stranger)).ShouldBeNull();
        (await store.AttachAsync(caseId, 103, stranger)).ShouldBe(CaseDocumentWriteResult.NotFound);
        (await store.DetachAsync(caseId, 101, stranger)).ShouldBe(CaseDocumentWriteResult.NotFound);

        (await store.DetachAsync(caseId, 101, owner)).ShouldBe(CaseDocumentWriteResult.Ok);
        (await store.DetachAsync(caseId, 101, owner)).ShouldBe(CaseDocumentWriteResult.NotLinked);
        (await store.ListAsync(caseId, owner)).ShouldNotBeNull().ShouldHaveSingleItem().DocumentId.ShouldBe(102);

        await using var db = factory.CreateDbContext();
        await db.Cases.ExecuteDeleteAsync();
        (await db.CaseDocumentLinks.CountAsync()).ShouldBe(0);
    }
}
