using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Data;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Entities;
using ISC.AI.Profile.Inspector.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Построчные правила доступа профиля «ИнспекторAI» (§2.1 ТЗ СКИД, этап 6 Э4-35) — <c>InspectorAccessPolicy</c>
/// поверх <c>DocumentStore.ListAsync</c>/<c>GetAsync</c>, реальный Postgres, обе схемы (<c>docflow</c> —
/// документы, <c>inspector</c> — роли). Проверяет все 4 роли + default-deny без роли, одним прогоном.
/// </summary>
public sealed class InspectorAccessPolicyTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = new PostgreSqlBuilder("pgvector/pgvector:pg16").Build();

    public Task InitializeAsync() => _postgres.StartAsync();

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();

    [Fact(DisplayName = "InspectorAccessPolicy: Администратор — ничего, Руководитель — всё, Инспектор/Исполнитель — только своё, без роли — ничего")]
    public async Task Policy_enforces_role_based_document_visibility()
    {
        var docFlowFactory = new DocFlowContextFactory(_postgres.GetConnectionString());
        var inspectorFactory = new InspectorContextFactory(_postgres.GetConnectionString());
        await using (var db = docFlowFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        await using (var db = inspectorFactory.CreateDbContext())
        {
            await db.Database.MigrateAsync();
        }

        var typeStore = new DocumentTypeStore(docFlowFactory);
        var policy = new InspectorAccessPolicy(inspectorFactory);
        var store = new DocumentStore(docFlowFactory, new TempFileStorage(), policy);

        var typeId = await typeStore.CreateAsync("Поручение", DocumentGroup.Execution, isActive: true);

        // Документ A: инспектор = 10, назначение исполнителю 20. Документ B: инспектор = 11, исполнителю 21.
        var docA = await store.CreateAsync(
            new DocumentDraft("A-1", new DateOnly(2026, 8, 6), typeId!.Value, DocumentDirection.Incoming,
                null, "Документ A", null, null, DocumentPriority.Medium, 10, 0, 5, 1),
            [new AssignmentDraft(5, 20, null)], useCommonDeadline: false, commonDeadline: null);
        var docB = await store.CreateAsync(
            new DocumentDraft("B-1", new DateOnly(2026, 8, 6), typeId.Value, DocumentDirection.Incoming,
                null, "Документ B", null, null, DocumentPriority.Medium, 11, 0, 5, 1),
            [new AssignmentDraft(5, 21, null)], useCommonDeadline: false, commonDeadline: null);
        docA.Status.ShouldBe(DocumentWriteStatus.Ok);
        docB.Status.ShouldBe(DocumentWriteStatus.Ok);

        // Роли: 10=Инспектор, 20=Исполнитель, 30=Руководитель, 40=Администратор; 50 — БЕЗ роли (не назначена).
        await using (var db = inspectorFactory.CreateDbContext())
        {
            db.UserRoleAssignments.AddRange(
                new UserRoleAssignment { UserId = 10, Role = UserRole.Inspector },
                new UserRoleAssignment { UserId = 20, Role = UserRole.Performer },
                new UserRoleAssignment { UserId = 21, Role = UserRole.Performer },
                new UserRoleAssignment { UserId = 30, Role = UserRole.Manager },
                new UserRoleAssignment { UserId = 40, Role = UserRole.Administrator });
            await db.SaveChangesAsync();
        }

        var floor = new AccessContext("0", MaxClassification: 9, AllowedDivisions: [5]);

        // Инспектор 10 — только «свой» документ A (назначен инспектором именно на нём).
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "10" }))
            .Select(d => d.RegNumber).ShouldBe(["A-1"]);

        // Исполнитель 20 — только документ A (там его назначение); исполнитель 21 — только B.
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "20" }))
            .Select(d => d.RegNumber).ShouldBe(["A-1"]);
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "21" }))
            .Select(d => d.RegNumber).ShouldBe(["B-1"]);

        // Руководитель 30 — оба документа (в пределах floor'а грифа/подразделения).
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "30" })).Count.ShouldBe(2);

        // Администратор 40 — ни одного документа (правило снятия доступа у привилегированной роли).
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "40" })).ShouldBeEmpty();

        // Без назначенной роли (50) — default-deny (ТБ-012), не «Руководитель по умолчанию».
        (await store.ListAsync(new DocumentListFilter(), floor with { SubjectId = "50" })).ShouldBeEmpty();

        // GetAsync — та же решётка: карточка чужого документа неотличима от «не найден».
        (await store.GetAsync(docA.DocumentId, floor with { SubjectId = "11" })).ShouldBeNull();
        (await store.GetAsync(docA.DocumentId, floor with { SubjectId = "10" })).ShouldNotBeNull();
    }

    private sealed class TempFileStorage : IDocFlowFileStorage
    {
        private readonly string _root = Path.Combine(
            Path.GetTempPath(), "iscai-inspector-policy-tests", Guid.NewGuid().ToString("N"));

        public async Task<string> SaveAsync(
            Stream content, string extension, string category, string subPath,
            CancellationToken cancellationToken = default)
        {
            var storedFileName = Guid.NewGuid().ToString("N") + extension;
            var directory = Path.Combine(_root, category, subPath);
            Directory.CreateDirectory(directory);
            await using var fileStream = File.Create(Path.Combine(directory, storedFileName));
            await content.CopyToAsync(fileStream, cancellationToken);
            return storedFileName;
        }

        public Task<Stream> OpenReadAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default) =>
            Task.FromResult<Stream>(File.OpenRead(Path.Combine(_root, category, subPath, storedFileName)));

        public Task DeleteAsync(
            string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
        {
            var path = Path.Combine(_root, category, subPath, storedFileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }

            return Task.CompletedTask;
        }
    }

    private sealed class DocFlowContextFactory(string connectionString) : IDbContextFactory<DocFlowDbContext>
    {
        public DocFlowDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<DocFlowDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", DocFlowDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }

    private sealed class InspectorContextFactory(string connectionString) : IDbContextFactory<InspectorDbContext>
    {
        public InspectorDbContext CreateDbContext() =>
            new(new DbContextOptionsBuilder<InspectorDbContext>()
                .UseNpgsql(connectionString, npg =>
                    npg.MigrationsHistoryTable("__ef_migrations_history", InspectorDbContext.Schema))
                .UseSnakeCaseNamingConvention()
                .Options);
    }
}
