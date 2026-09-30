using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Accounts;
using ISC.AI.Modules.Admin.Application.Features.Roles;
using ISC.AI.Modules.Admin.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Экран «Пользователи» (карточка сотрудника): назначение роли через порт профиля, создание сотрудника с должностью,
/// карточка с ролью и допуском, история из журнала аудита. Право на каждое действие даёт профиль (ТБ-012), без права
/// порты не опрашиваются.
/// </summary>
public sealed class UserAdministrationTests
{
    private const int CallerId = 1;
    private const int UserId = 2;

    private readonly IUserAccountStore _accounts = Substitute.For<IUserAccountStore>();
    private readonly IUserRoleCatalog _roles = Substitute.For<IUserRoleCatalog>();
    private readonly IDivisionCatalog _divisions = Substitute.For<IDivisionCatalog>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();
    private readonly IAuditReader _audit = Substitute.For<IAuditReader>();
    private readonly IAccessContextProvider _access = Substitute.For<IAccessContextProvider>();

    private static readonly UserAccountRow Ashyrov = new(
        UserId, "ashyrov", "Ашыров Бактилек Дамирович", IsActive: true, HasLocalPassword: true, MustChangePassword: false,
        Position: "Старший эксперт", MaxClassification: 2, Divisions: [1, 99]);

    public UserAdministrationTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)CallerId);
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(true);
        _administration.CanViewAuditAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListRolesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new RoleOption("Administrator", "Администратор", "Всё"),
            new RoleOption("Verifier", "Верификатор", "Вторая подпись"),
        ]);
        _roles.GetUserRoleKeysAsync(Arg.Any<CancellationToken>())
            .Returns(new Dictionary<int, string> { [UserId] = "Verifier" });
        _divisions.ListAsync(Arg.Any<CancellationToken>()).Returns([new DivisionCatalogItem(1, "7Управление", true)]);
        _accounts.SearchAsync(Arg.Is<UserAccountFilter>(f => f.RestrictToUserIds != null && f.RestrictToUserIds.Contains(UserId)), Arg.Any<CancellationToken>())
            .Returns(new UserAccountPage([Ashyrov], 1));
        _accounts.SearchAsync(Arg.Is<UserAccountFilter>(f => f.RestrictToUserIds != null && !f.RestrictToUserIds.Contains(UserId)), Arg.Any<CancellationToken>())
            .Returns(new UserAccountPage([], 0));
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns(new AccessContext("1", MaxClassification: 3, AllowedDivisions: [1]));
    }

    // --- Назначение роли ---

    [Fact(DisplayName = "Роль: без права администрирования — отказ, порт профиля не вызывается (ТБ-012)")]
    public async Task Assign_role_without_right_is_denied()
    {
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await new AssignUserRoleCommand.Handler(_roles, _administration)
            .Handle(new AssignUserRoleCommand(UserId, "Administrator"), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe(AdminGuard.Denied);
        await _roles.DidNotReceive().AssignAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Роль: отказ профиля (последний Администратор) виден тем же текстом")]
    public async Task Assign_role_shows_profile_refusal()
    {
        _roles.AssignAsync(UserId, null, Arg.Any<CancellationToken>()).Returns(RoleAssignmentResult.Fail("Нельзя снять последнего"));

        var response = await new AssignUserRoleCommand.Handler(_roles, _administration)
            .Handle(new AssignUserRoleCommand(UserId, null), CancellationToken.None);

        response.Status.ShouldBeFalse();
        response.StatusMessage.ShouldBe("Нельзя снять последнего");
    }

    [Fact(DisplayName = "Роль: успех; сводка журнала «admin:role:{пользователь}:{ключ}», снятие — «снята»")]
    public async Task Assign_role_succeeds_with_audit_summary()
    {
        _roles.AssignAsync(UserId, "Administrator", Arg.Any<CancellationToken>()).Returns(RoleAssignmentResult.Ok);

        var response = await new AssignUserRoleCommand.Handler(_roles, _administration)
            .Handle(new AssignUserRoleCommand(UserId, "Administrator"), CancellationToken.None);

        response.Status.ShouldBeTrue();
        new AssignUserRoleCommand(UserId, "Administrator").AuditSummary.ShouldBe("admin:role:2:Administrator");
        new AssignUserRoleCommand(UserId, null).AuditSummary.ShouldBe("admin:role:2:снята");
    }

    // --- Создание сотрудника ---

    [Fact(DisplayName = "Новый сотрудник: номер и временный пароль; должность сохраняется второй правкой")]
    public async Task Create_account_returns_id_and_saves_position()
    {
        _accounts.CreateAsync("ivanov", "Иванов И. И.", Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((int?)7);

        var response = await new CreateUserAccountCommand.Handler(_accounts, _administration)
            .Handle(new CreateUserAccountCommand("ivanov", "Иванов И. И.", " Следователь "), CancellationToken.None);

        response.Status.ShouldBeTrue();
        response.Data.ShouldNotBeNull().UserId.ShouldBe(7);
        response.Data.TemporaryPassword.ShouldNotBeNullOrWhiteSpace();
        await _accounts.Received(1).UpdateProfileAsync(7, "Иванов И. И.", "Следователь", Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Новый сотрудник: без должности правки нет; занятое имя входа — отказ")]
    public async Task Create_account_without_position_and_taken_login()
    {
        _accounts.CreateAsync("petrov", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((int?)8);
        _accounts.CreateAsync("taken", Arg.Any<string?>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns((int?)null);
        var handler = new CreateUserAccountCommand.Handler(_accounts, _administration);

        (await handler.Handle(new CreateUserAccountCommand("petrov", null), CancellationToken.None)).Status.ShouldBeTrue();
        await _accounts.DidNotReceive().UpdateProfileAsync(Arg.Any<int>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());

        var taken = await handler.Handle(new CreateUserAccountCommand("taken", null), CancellationToken.None);
        taken.Status.ShouldBeFalse();
        taken.StatusMessage.ShouldBe("Имя входа уже занято.");
    }

    // --- Карточка ---

    [Fact(DisplayName = "Карточка: роль с подписью, подразделения допуска с наименованиями, неизвестный номер помечен")]
    public async Task Card_contains_role_and_clearance()
    {
        _administration.IsInitialSetupAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await CardHandler().Handle(new GetUserAccountQuery(UserId), CancellationToken.None);

        var card = response.Data.ShouldNotBeNull();
        card.Account.RoleKey.ShouldBe("Verifier");
        card.Account.RoleLabel.ShouldBe("Верификатор");
        card.Roles.Count.ShouldBe(2);
        card.ClearanceDivisions.Select(d => (d.Id, d.IsKnown)).ShouldBe([(1, true), (99, false)]);
        card.IsSelf.ShouldBeFalse();
    }

    [Fact(DisplayName = "Карточка: своя — IsSelf; без права — отказ; нет такой учётной записи — «не найдена»")]
    public async Task Card_self_denied_and_missing()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)UserId);
        (await CardHandler().Handle(new GetUserAccountQuery(UserId), CancellationToken.None)).Data.ShouldNotBeNull().IsSelf.ShouldBeTrue();

        (await CardHandler().Handle(new GetUserAccountQuery(404), CancellationToken.None)).Status.ShouldBeFalse();

        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(false);
        _accounts.ClearReceivedCalls();
        var denied = await CardHandler().Handle(new GetUserAccountQuery(UserId), CancellationToken.None);
        denied.StatusMessage.ShouldBe(AdminGuard.Denied);
        await _accounts.DidNotReceive().SearchAsync(Arg.Any<UserAccountFilter>(), Arg.Any<CancellationToken>());
    }

    // --- История ---

    [Fact(DisplayName = "История: без права читать журнал — отказ; без допуска — объяснение, а не падение")]
    public async Task History_requires_audit_right_and_clearance()
    {
        _administration.CanViewAuditAsync(Arg.Any<CancellationToken>()).Returns(false);
        (await HistoryHandler().Handle(new GetUserHistoryQuery(UserId), CancellationToken.None)).StatusMessage.ShouldBe(AdminGuard.AuditDenied);

        _administration.CanViewAuditAsync(Arg.Any<CancellationToken>()).Returns(true);
        _access.GetCurrentAsync(Arg.Any<CancellationToken>()).Returns<AccessContext>(_ => throw new AccessContextRequiredException());
        (await HistoryHandler().Handle(new GetUserHistoryQuery(UserId), CancellationToken.None)).StatusMessage.ShouldBe(AdminGuard.AuditClearanceRequired);
    }

    [Fact(DisplayName = "История: записи о сотруднике по-русски, новые первыми, без повторов; чужие не попадают")]
    public async Task History_is_translated_and_ordered()
    {
        var t0 = new DateTime(2026, 9, 29, 10, 0, 0, DateTimeKind.Utc);
        var rows = new[]
        {
            Row(1, t0, "admin:account:create:ashyrov"),
            Row(2, t0.AddHours(1), "admin:clearance:2:set:grif=2;divisions=1"),
            Row(3, t0.AddHours(2), "admin:role:2:Verifier"),
            Row(4, t0.AddHours(3), "admin:account:20:disable"),
        };
        _audit.QueryAsync(Arg.Any<AuditFilter>(), Arg.Any<AccessContext>(), Arg.Any<CancellationToken>())
            .Returns(new AuditPage(rows, rows.Length));

        var response = await HistoryHandler().Handle(new GetUserHistoryQuery(UserId), CancellationToken.None);

        response.Data.ShouldNotBeNull().Select(e => e.What).ShouldBe(
        [
            "Роль: Верификатор",
            "Допуск: Секретно; подразделения: 7Управление",
            "Учётная запись создана, выдан временный пароль",
        ]);
        new GetUserHistoryQuery(UserId).AuditSummary.ShouldBe("admin:user-history:2:view");
    }

    // --- Перевод сводок журнала ---

    [Theory(DisplayName = "Сводка журнала о сотруднике переводится; чужая, похожая по началу, — нет")]
    [InlineData("admin:account:2:reset-password", 1, "Сброшен пароль, выдан временный")]
    [InlineData("admin:account:2:disable", 1, "Учётная запись отключена")]
    [InlineData("admin:account:2:enable", 1, "Учётная запись включена")]
    [InlineData("admin:account:2:update-profile", 1, "Изменены ФИО или должность")]
    [InlineData("admin:clearance:2:revoke", 1, "Допуск отозван")]
    [InlineData("admin:clearance:2:set:grif=4;divisions=", 1, "Допуск: Особой важности; подразделения: ни одного")]
    [InlineData("admin:clearance:2:set:grif=1;divisions=1,7", 1, "Допуск: ДСП; подразделения: 7Управление, № 7")]
    [InlineData("admin:role:2:снята", 1, "Роль снята")]
    [InlineData("investigation:user-role:2:Verifier", 1, "Роль: Верификатор")]
    [InlineData("admin:account:change-own-password", 2, "Сменил собственный пароль")]
    [InlineData("admin:account:change-own-password", 1, null)]
    [InlineData("admin:account:20:disable", 1, null)]
    [InlineData("admin:account:create:ashyrov2", 1, null)]
    [InlineData("investigation:person:2:view", 1, null)]
    [InlineData(null, 1, null)]
    public void History_text_describes_only_this_user(string? objectRef, int subjectId, string? expected)
    {
        var described = UserHistoryText.Describe(
            objectRef, subjectId, UserId, "ashyrov",
            new Dictionary<string, string> { ["Verifier"] = "Верификатор" },
            new Dictionary<int, string> { [1] = "7Управление" });

        described?.What.ShouldBe(expected);
        if (expected is null)
        {
            described.ShouldBeNull();
        }
    }

    private GetUserAccountQuery.Handler CardHandler() => new(_accounts, _roles, _divisions, _subject, _administration);

    private GetUserHistoryQuery.Handler HistoryHandler() => new(_audit, _access, _accounts, _roles, _divisions, _administration);

    private static AuditRecordRow Row(long id, DateTime at, string objectRef) =>
        new(id, at, CallerId, "Администратор", AuditAction.Modify, objectRef, 0, null, null);
}
