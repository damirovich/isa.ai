using System.Threading.Tasks;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Shouldly;
using Testcontainers.PostgreSql;

namespace ISC.AI.IntegrationTests.Persistence;

/// <summary>
/// Один контейнер на класс: роли назначены один раз, каждая строка матрицы читает их через настоящий
/// <see cref="UserRoleStore"/>. Пользователи 10/20/30/40/41/60 — по одному на роль, 50 — без роли.
/// </summary>
public sealed class InvestigationRoleMatrixFixture : IAsyncLifetime
{
    private readonly PostgreSqlContainer _postgres = TestPostgres.Create();

    public UserRoleStore Roles { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        await _postgres.StartAsync();
        var factory = new InvestigationContextFactory(_postgres.GetConnectionString());
        var core = new CoreContextFactory(_postgres.GetConnectionString());
        await InvestigationTestKit.MigrateAsync(factory);
        await InvestigationTestKit.AssignRolesAsync(factory,
            (10, InvestigationRole.Investigator),
            (20, InvestigationRole.Head),
            (30, InvestigationRole.Administrator),
            (40, InvestigationRole.FaceExpert),
            (41, InvestigationRole.Verifier),
            (60, InvestigationRole.SecurityOfficer));
        Roles = new UserRoleStore(core, factory);
    }

    public Task DisposeAsync() => _postgres.DisposeAsync().AsTask();
}

/// <summary>
/// Матрица прав ролей на операции пакета «Медиа» СТРОГО по ТП-004 (ADR-0022, п. 8): по всем шести ролям
/// и «без роли» с явными ожиданиями. Любое расширение матрицы (например, поиск Руководителем или
/// Администратором) ломает этот тест и требует правки ADR — так и задумано.
/// </summary>
public sealed class InvestigationRoleMatrixTests(InvestigationRoleMatrixFixture fixture) : IClassFixture<InvestigationRoleMatrixFixture>
{
    [Theory(DisplayName = "ТП-004: загрузка — Следователь; поиск — Следователь и Эксперт по лицам; удаление — Администратор и Руководитель; остальным и без роли — ничего")]
    [InlineData(InvestigationRole.Investigator, true, true, false)]
    [InlineData(InvestigationRole.FaceExpert, false, true, false)]
    [InlineData(InvestigationRole.Verifier, false, false, false)]
    [InlineData(InvestigationRole.Head, false, false, true)]
    [InlineData(InvestigationRole.Administrator, false, false, true)]
    [InlineData(InvestigationRole.SecurityOfficer, false, false, false)]
    [InlineData(null, false, false, false)]
    public async Task Media_rights_matrix_matches_tp004(InvestigationRole? role, bool canUpload, bool canSearch, bool canPurge)
    {
        var userId = role switch
        {
            InvestigationRole.Investigator => 10,
            InvestigationRole.Head => 20,
            InvestigationRole.Administrator => 30,
            InvestigationRole.FaceExpert => 40,
            InvestigationRole.Verifier => 41,
            InvestigationRole.SecurityOfficer => 60,
            _ => 50,
        };

        (await fixture.Roles.GetRoleAsync(userId)).ShouldBe(role);

        var media = new MediaAdministration(fixture.Roles, new FixedSubjectProvider(userId));
        (await media.CanUploadAsync()).ShouldBe(canUpload, $"загрузка, роль {role?.ToString() ?? "нет"}");
        (await media.CanSearchAsync()).ShouldBe(canSearch, $"поиск, роль {role?.ToString() ?? "нет"}");
        (await media.CanPurgeAsync()).ShouldBe(canPurge, $"удаление, роль {role?.ToString() ?? "нет"}");
    }
}
