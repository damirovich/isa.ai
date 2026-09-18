using ISC.AI.Modules.Media.Domain.Model;
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
/// Матрица прав ролей на операции пакета «Медиа» (ADR-0022, п. 8): по всем шести ролям
/// и «без роли» с явными ожиданиями. Матрица включает ОТКЛОНЕНИЕ от ТП-004 по решению заказчика
/// (2026-09-18, ADR-0022 п. 8): Администратору открыт весь функционал профиля, потому что роль у
/// пользователя одна и последнего Администратора снять нельзя. Любое ДАЛЬНЕЙШЕЕ расширение (например,
/// поиск Руководителем) ломает этот тест и требует правки ADR — так и задумано. Правило двух лиц
/// (ТБ-073) матрицей не затрагивается: оно в модуле и проверяется GATE-5.
/// </summary>
public sealed class InvestigationRoleMatrixTests(InvestigationRoleMatrixFixture fixture) : IClassFixture<InvestigationRoleMatrixFixture>
{
    [Theory(DisplayName = "Матрица прав: загрузка — Следователь и Администратор; поиск — Следователь, Эксперт по лицам и Администратор; удаление — Администратор и Руководитель; остальным и без роли — ничего")]
    [InlineData(InvestigationRole.Investigator, true, true, false)]
    [InlineData(InvestigationRole.FaceExpert, false, true, false)]
    [InlineData(InvestigationRole.Verifier, false, false, false)]
    [InlineData(InvestigationRole.Head, false, false, true)]
    [InlineData(InvestigationRole.Administrator, true, true, true)]
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

    [Theory(DisplayName = "Стадии верификации по ролям: эксперт — Эксперт по лицам и Администратор, верификатор — Верификатор и Администратор; остальным и без роли — отказ")]
    [InlineData(InvestigationRole.FaceExpert, true, false)]
    [InlineData(InvestigationRole.Verifier, false, true)]
    [InlineData(InvestigationRole.Administrator, true, true)]
    [InlineData(InvestigationRole.Investigator, false, false)]
    [InlineData(InvestigationRole.Head, false, false)]
    [InlineData(InvestigationRole.SecurityOfficer, false, false)]
    [InlineData(null, false, false)]
    public async Task Verification_stages_match_roles(InvestigationRole? role, bool canExpert, bool canVerifier)
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

        // Администратору открыты ОБЕ стадии (решение заказчика 2026-09-18, ADR-0022 п. 8). Правило двух
        // лиц этим не ослаблено: обе стадии ОДНОГО кандидата одним субъектом отклоняет TwoPersonRule —
        // он в модуле, профилем не переопределяется, проверяется VerificationGate5Tests.
        var policy = new VerificationPolicy(fixture.Roles);
        (await policy.CanActAsync(VerificationStage.Expert, userId)).ShouldBe(canExpert, $"эксперт, роль {role?.ToString() ?? "нет"}");
        (await policy.CanActAsync(VerificationStage.Verifier, userId)).ShouldBe(canVerifier, $"верификатор, роль {role?.ToString() ?? "нет"}");
    }
}
