using System;
using System.Linq;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Порт пакета «Медиа» <see cref="ICaseScope"/> в реализации профиля «Следствие» (<c>CaseScope</c>):
/// область дел субъекта (ТБ-071), основания поиска, носители дел, запись появления (ТБ-073).
/// Реальный Postgres через Testcontainers.
/// </summary>
public sealed class InvestigationCaseScopeTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "ICaseScope: доступные дела и основания — под решёткой; носители дел и дело носителя — по привязкам; появление — через IPersonStore")]
    public async Task Case_scope_exposes_cases_authorizations_assets_and_appearances()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var persons = InvestigationTestKit.CreatePersonStore(factory, core);
        var scope = InvestigationTestKit.CreateCaseScope(factory, core);

        var owner = InvestigationTestKit.Access(10, 9, 5);
        var stranger = InvestigationTestKit.Access(11, 9, 5);

        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), owner);
        var caseB = await cases.CreateAsync(InvestigationTestKit.Draft("B-1", 5, 2, 10), owner);
        caseA.Result.ShouldBe(CaseWriteResult.Ok);
        caseB.Result.ShouldBe(CaseWriteResult.Ok);

        (await cases.AddAuthorizationAsync(
            new SearchAuthorizationDraft(caseA.CaseId, AuthorizationKind.InvestigatorOrder, "Поручение № 5", new DateOnly(2026, 9, 1), 10, null, null),
            owner)).Result.ShouldBe(CaseWriteResult.Ok);
        (await cases.AddAuthorizationAsync(
            new SearchAuthorizationDraft(caseA.CaseId, AuthorizationKind.OperativeMeasure, "ОРМ № 7", new DateOnly(2026, 9, 2), 10, null, null),
            owner)).Result.ShouldBe(CaseWriteResult.Ok);

        // Доступные дела и карточка — только субъекту дела; чужому — пусто/null (ТБ-071, ТБ-021).
        (await scope.ListAccessibleCasesAsync(owner)).Select(c => c.Number).OrderBy(n => n).ShouldBe(["A-1", "B-1"]);
        (await scope.ListAccessibleCasesAsync(stranger)).ShouldBeEmpty();
        var item = (await scope.GetCaseAsync(caseA.CaseId, owner)).ShouldNotBeNull();
        item.Classification.ShouldBe<short>(2);
        item.DivisionId.ShouldBe(5);
        (await scope.GetCaseAsync(caseA.CaseId, stranger)).ShouldBeNull();

        // Основания — реквизиты для аудита (ТБ-072); чужому — пусто.
        (await scope.ListAuthorizationsAsync(caseA.CaseId, owner)).Select(a => a.Reference).OrderBy(r => r)
            .ShouldBe(["ОРМ № 7", "Поручение № 5"]);
        (await scope.ListAuthorizationsAsync(caseA.CaseId, stranger)).ShouldBeEmpty();
        (await scope.ListAuthorizationsAsync(caseB.CaseId, owner)).ShouldBeEmpty();

        // Привязка носителей идемпотентна; область поиска = носители выбранных дел.
        await scope.LinkAssetAsync(caseA.CaseId, 100, "ул. Ленина, 1", 10);
        await scope.LinkAssetAsync(caseA.CaseId, 100, null, 10);
        await scope.LinkAssetAsync(caseA.CaseId, 101, null, 10);
        await scope.LinkAssetAsync(caseB.CaseId, 200, null, 10);

        (await scope.GetAssetIdsAsync([caseA.CaseId])).OrderBy(id => id).ShouldBe([100, 101]);
        (await scope.GetAssetIdsAsync([caseA.CaseId, caseB.CaseId])).OrderBy(id => id).ShouldBe([100, 101, 200]);
        (await scope.GetAssetIdsAsync([])).ShouldBeEmpty();
        (await scope.GetCaseIdForAssetAsync(200, owner)).ShouldBe(caseB.CaseId);
        (await scope.GetCaseIdForAssetAsync(999, owner)).ShouldBeNull();
        await Should.ThrowAsync<InvalidOperationException>(() => scope.LinkAssetAsync(999_999, 300, null, 10));

        var details = (await cases.GetAsync(caseA.CaseId, owner)).ShouldNotBeNull();
        details.Media.Count.ShouldBe(2);
        details.Media.Single(m => m.MediaAssetId == 100).Place.ShouldBe("ул. Ленина, 1");

        // Фигуранты дела — подпись для привязки кандидата; появление — через IPersonStore.
        var person = await persons.CreateAsync(new PersonDraft(caseA.CaseId, null, true, null, null), owner);
        (await scope.ListPersonsAsync(caseA.CaseId, owner)).ShouldHaveSingleItem().DisplayName.ShouldBe("Неустановленное лицо № 1");
        (await scope.ListPersonsAsync(caseA.CaseId, stranger)).ShouldBeEmpty();

        var confirmed = new ConfirmedAppearance(
            caseA.CaseId, person.PersonId, SessionId: 5, CandidateId: 51, FaceId: 1000, AssetId: 100,
            FrameIndex: null, FrameTimestampMs: null, Similarity: 0.87, Classification: 2, DivisionId: 5,
            ExpertUserId: 40, VerifierUserId: 41, ConfirmedAtUtc: DateTime.UtcNow);
        await scope.RecordAppearanceAsync(confirmed);

        // ТБ-073: эксперт и верификатор — разные люди, иначе отказ ещё до хранилища.
        await Should.ThrowAsync<InvalidOperationException>(
            () => scope.RecordAppearanceAsync(confirmed with { CandidateId = 52, VerifierUserId = 40 }));

        var appearances = await persons.ListAppearancesAsync(person.PersonId, owner);
        var appearance = appearances.ShouldHaveSingleItem();
        appearance.Status.ShouldBe(AppearanceStatus.InvestigativeLead);
        appearance.CandidateId.ShouldBe(51);
        appearance.SearchSessionId.ShouldBe(5);
        appearance.MediaAssetId.ShouldBe(100);
        appearance.ExpertUserId.ShouldBe(40);
        appearance.VerifierUserId.ShouldBe(41);
    }

    [Fact(DisplayName = "ICaseScope: носитель доступен только через ДОСТУПНОЕ дело — следователь не видит носитель чужого дела того же подразделения, руководитель видит, без роли — ничего; из двух привязок наружу выдаётся доступное дело")]
    public async Task Asset_accessibility_follows_case_lattice_and_role()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (11, InvestigationRole.Investigator),
            (20, InvestigationRole.Head));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var scope = InvestigationTestKit.CreateCaseScope(factory, core);

        // Одно подразделение (5), один гриф (2), одинаковый допуск — различие только в роли и владении делом.
        var investigatorA = InvestigationTestKit.Access(10, 9, 5);
        var investigatorB = InvestigationTestKit.Access(11, 9, 5);
        var head = InvestigationTestKit.Access(20, 9, 5);
        var noRole = InvestigationTestKit.Access(50, 9, 5);

        var caseA = await cases.CreateAsync(InvestigationTestKit.Draft("A-1", 5, 2, 10), head);
        var caseB = await cases.CreateAsync(InvestigationTestKit.Draft("B-1", 5, 2, 11), head);
        caseA.Result.ShouldBe(CaseWriteResult.Ok);
        caseB.Result.ShouldBe(CaseWriteResult.Ok);

        // 100 — только в деле A; 200 — только в деле B; 300 — в обоих (дедупликация по хешу), сначала A.
        await scope.LinkAssetAsync(caseA.CaseId, 100, null, 10);
        await scope.LinkAssetAsync(caseB.CaseId, 200, null, 11);
        await scope.LinkAssetAsync(caseA.CaseId, 300, null, 10);
        await scope.LinkAssetAsync(caseB.CaseId, 300, null, 11);

        // Следователь A: своё дело — да, чужое (тот же отдел, тот же допуск) — нет (ТБ-071, ТФ-ДЕЛ-03).
        (await scope.IsAssetAccessibleAsync(100, investigatorA)).ShouldBeTrue();
        (await scope.IsAssetAccessibleAsync(200, investigatorA)).ShouldBeFalse();
        (await scope.IsAssetAccessibleAsync(300, investigatorA)).ShouldBeTrue();
        (await scope.GetCaseIdForAssetAsync(100, investigatorA)).ShouldBe(caseA.CaseId);
        (await scope.GetCaseIdForAssetAsync(200, investigatorA)).ShouldBeNull();

        // Следователь B: носитель 300 привязан к A (недоступно) и B (доступно) → наружу только B (ТБ-020/021).
        (await scope.IsAssetAccessibleAsync(100, investigatorB)).ShouldBeFalse();
        (await scope.IsAssetAccessibleAsync(300, investigatorB)).ShouldBeTrue();
        (await scope.GetCaseIdForAssetAsync(300, investigatorB)).ShouldBe(caseB.CaseId);
        (await scope.GetCaseIdForAssetAsync(100, investigatorB)).ShouldBeNull();

        // Руководитель — дела подразделения целиком.
        (await scope.IsAssetAccessibleAsync(100, head)).ShouldBeTrue();
        (await scope.IsAssetAccessibleAsync(200, head)).ShouldBeTrue();
        (await scope.GetCaseIdForAssetAsync(300, head)).ShouldBe(caseA.CaseId);

        // Без роли — default-deny (ТБ-012), даже при достаточном допуске.
        (await scope.IsAssetAccessibleAsync(100, noRole)).ShouldBeFalse();
        (await scope.GetCaseIdForAssetAsync(100, noRole)).ShouldBeNull();

        // Floor ядра поверх роли: руководитель с допуском ниже грифа или чужим подразделением — нет.
        (await scope.IsAssetAccessibleAsync(100, InvestigationTestKit.Access(20, 1, 5))).ShouldBeFalse();
        (await scope.IsAssetAccessibleAsync(100, InvestigationTestKit.Access(20, 9, 6))).ShouldBeFalse();

        // Непривязанный носитель неотличим от недоступного.
        (await scope.IsAssetAccessibleAsync(999, head)).ShouldBeFalse();
        (await scope.GetCaseIdForAssetAsync(999, head)).ShouldBeNull();
    }

    [Fact(DisplayName = "ТБ-074/ADR-0024: при регламенте удаления по закрытию биометрию закрытого дела строить нельзя; пока открыто хотя бы одно дело носителя — можно; при хранении шаблонов запрета нет вовсе")]
    public async Task Biometric_indexing_is_forbidden_for_closed_cases()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);

        // Регламент включён: шаблоны удаляются по закрытию, значит их нельзя построить заново.
        var scope = InvestigationTestKit.CreateCaseScope(factory, core, purgeTemplatesOnCaseClosure: true);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var open = await cases.CreateAsync(InvestigationTestKit.Draft("OPEN-1", 5, 2, 10), owner);
        var closing = await cases.CreateAsync(InvestigationTestKit.Draft("CLOSE-1", 5, 2, 10), owner);

        // Носитель 100 — только в закрываемом деле; носитель 200 — в обоих (дедупликация по хешу, ТФ-МЕД-04).
        await scope.LinkAssetAsync(closing.CaseId, 100, null, 10);
        await scope.LinkAssetAsync(closing.CaseId, 200, null, 10);
        await scope.LinkAssetAsync(open.CaseId, 200, null, 10);

        // Пока дела открыты — индексация разрешена обоим, и непривязанному носителю тоже (запрета нет).
        (await scope.IsBiometricIndexingAllowedAsync(100)).ShouldBeTrue();
        (await scope.IsBiometricIndexingAllowedAsync(200)).ShouldBeTrue();
        (await scope.IsBiometricIndexingAllowedAsync(999)).ShouldBeTrue();

        (await cases.SetStatusAsync(closing.CaseId, CaseStatus.Closed, owner)).ShouldBe(CaseWriteResult.Ok);

        // Носитель закрытого дела — шаблоны сняты регламентом, заново строить нельзя.
        (await scope.IsBiometricIndexingAllowedAsync(100)).ShouldBeFalse();

        // А носитель, который есть и в ОТКРЫТОМ деле, индексируется: там основание ещё действует.
        (await scope.IsBiometricIndexingAllowedAsync(200)).ShouldBeTrue();

        // Возврат дела в производство снимает запрет (регламент исполнялся по факту закрытия).
        (await cases.SetStatusAsync(closing.CaseId, CaseStatus.InProgress, owner)).ShouldBe(CaseWriteResult.Ok);
        (await scope.IsBiometricIndexingAllowedAsync(100)).ShouldBeTrue();

        // А в поставке по умолчанию (шаблоны ХРАНЯТСЯ, ADR-0024) запрещать нечего: закрытие дела ничего
        // не удаляет, и переиндексация не возвращает биометрию из небытия.
        var keeping = InvestigationTestKit.CreateCaseScope(factory, core);
        (await cases.SetStatusAsync(closing.CaseId, CaseStatus.Closed, owner)).ShouldBe(CaseWriteResult.Ok);
        (await keeping.IsBiometricIndexingAllowedAsync(100)).ShouldBeTrue();
    }

    [Fact(DisplayName = "ТФ-ПЛ-05: закрытые дела помечены в области поиска — модуль сам решает, брать ли их")]
    public async Task Closed_cases_are_flagged_for_scope()
    {
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory, (10, InvestigationRole.Investigator));

        var cases = InvestigationTestKit.CreateCaseStore(factory, core);
        var scope = InvestigationTestKit.CreateCaseScope(factory, core);
        var owner = InvestigationTestKit.Access(10, 9, 5);

        var open = await cases.CreateAsync(InvestigationTestKit.Draft("OPEN-2", 5, 2, 10), owner);
        var closed = await cases.CreateAsync(InvestigationTestKit.Draft("CLOSED-2", 5, 2, 10), owner);
        (await cases.SetStatusAsync(closed.CaseId, CaseStatus.Closed, owner)).ShouldBe(CaseWriteResult.Ok);

        // Признак нужен модулю, чтобы не тащить оконченные дела в область поиска без ведома оператора:
        // само решение принимает модуль (ТФ-ПЛ-05), профиль лишь сообщает состояние дела.
        var items = await scope.ListAccessibleCasesAsync(owner);
        items.Single(c => c.CaseId == open.CaseId).IsClosed.ShouldBeFalse();
        items.Single(c => c.CaseId == closed.CaseId).IsClosed.ShouldBeTrue();

        (await scope.GetCaseAsync(closed.CaseId, owner)).ShouldNotBeNull().IsClosed.ShouldBeTrue();
        (await scope.GetCaseAsync(open.CaseId, owner)).ShouldNotBeNull().IsClosed.ShouldBeFalse();
    }
}
