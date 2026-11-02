using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Application;
using ISC.AI.Modules.Admin.Application.Features.Accounts;
using ISC.AI.Modules.Admin.Application.Features.Audit;
using ISC.AI.Modules.Admin.Domain.Services;
using ISC.AI.Modules.Admin.UI;
using ISC.AI.Persistence.Security;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Admin;

/// <summary>
/// Блокировка учётной записи после неудачных входов подряд (ТБ-081, ADR-0065) на стороне администрирования: снять её
/// может только Администратор, состояние видно в списке и карточке, блокировка и снятие — в истории сотрудника и в журнале
/// по-русски; порог блокировки выключить настройкой нельзя.
/// </summary>
public sealed class AccountLockoutAdminTests
{
    private const int UserId = 7;

    private static readonly IReadOnlyDictionary<string, string> NoRoles = new Dictionary<string, string>();
    private static readonly IReadOnlyDictionary<int, string> NoDivisions = new Dictionary<int, string>();

    private readonly IUserAccountStore _accounts = Substitute.For<IUserAccountStore>();
    private readonly IPlatformAdministration _administration = Substitute.For<IPlatformAdministration>();

    [Fact(DisplayName = "ТБ-081: снять блокировку без права администрирования нельзя — хранилище не вызывается")]
    public async Task Unlock_requires_administration_right()
    {
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(false);

        var response = await Handler().Handle(new UnlockUserAccountCommand(UserId), CancellationToken.None);

        response.Data.ShouldBeFalse();
        response.StatusMessage.ShouldBe(AdminGuard.Denied);
        await _accounts.DidNotReceiveWithAnyArgs().UnlockAsync(default, default);
    }

    [Fact(DisplayName = "ТБ-081: Администратор снимает блокировку; нет учётной записи — «не найдена»; в журнал — «admin:account:{id}:unlock»")]
    public async Task Unlock_by_administrator_is_audited()
    {
        _administration.CanManageAsync(Arg.Any<CancellationToken>()).Returns(true);
        _accounts.UnlockAsync(UserId, Arg.Any<CancellationToken>()).Returns(true);

        var command = new UnlockUserAccountCommand(UserId);
        (await Handler().Handle(command, CancellationToken.None)).Data.ShouldBeTrue();
        command.AuditAction.ShouldBe(AuditAction.Modify);
        command.AuditSummary.ShouldBe("admin:account:7:unlock");

        (await Handler().Handle(new UnlockUserAccountCommand(99), CancellationToken.None)).StatusMessage
            .ShouldBe("Учётная запись не найдена.");
    }

    [Fact(DisplayName = "ТБ-081: в истории сотрудника — «заблокирована после N неудачных входов» и «снята блокировка»; записи о другом сотруднике не попадают")]
    public void History_describes_lockout_and_unlock()
    {
        var locked = UserHistoryText.Describe("auth:account:7:lockout:attempts=5", null, UserId, "sidorov", NoRoles, NoDivisions);
        locked.ShouldNotBeNull().What.ShouldBe("Заблокирована после 5 неудачных входов подряд");
        locked.Value.Kind.ShouldBe(UserHistoryKind.Danger);

        var unlocked = UserHistoryText.Describe("admin:account:7:unlock", 1, UserId, "sidorov", NoRoles, NoDivisions);
        unlocked.ShouldNotBeNull().What.ShouldBe("Снята блокировка после неудачных входов");
        unlocked.Value.Kind.ShouldBe(UserHistoryKind.Success);

        UserHistoryText.Describe("auth:account:77:lockout:attempts=5", null, UserId, "sidorov", NoRoles, NoDivisions).ShouldBeNull();
    }

    [Fact(DisplayName = "ТБ-081: в журнале аудита блокировка и снятие — по-русски")]
    public void Journal_describes_lockout_and_unlock()
    {
        AuditText.Describe(AuditAction.Modify, "auth:account:7:lockout:attempts=5", null)
            .ShouldBe("Вход в систему · учётная запись · № 7 · блокировка после неудачных входов · неудачных входов подряд: 5");
        AuditText.Describe(AuditAction.Modify, "admin:account:7:unlock", null)
            .ShouldBe("Администрирование · учётная запись · № 7 · снятие блокировки");
    }

    [Fact(DisplayName = "ТБ-081: заблокированная учётная запись в списке и карточке — «заблокирована»; отключение важнее")]
    public void Status_shows_lockout()
    {
        var account = new UserAccountRow(UserId, "sidorov", "Сидоров", IsActive: true, HasLocalPassword: true, MustChangePassword: false);

        AdminUi.Status(account).Label.ShouldBe("активна");
        AdminUi.Status(account with { LockedOutAt = new System.DateTime(2026, 11, 2, 9, 0, 0, System.DateTimeKind.Utc) }).Label
            .ShouldBe("заблокирована");
        AdminUi.Status(account with { IsActive = false, LockedOutAt = System.DateTime.UtcNow }).Label.ShouldBe("отключена");
    }

    [Fact(DisplayName = "ТБ-081: порог — 5 по умолчанию; ноль, минус и мусор — тоже 5: выключить блокировку настройкой нельзя")]
    public void Threshold_cannot_be_disabled_by_configuration()
    {
        static int Read(string? value) => LoginLockout.ReadThreshold(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [LoginLockout.ThresholdKey] = value }).Build());

        Read(null).ShouldBe(5);
        Read("3").ShouldBe(3);
        Read("0").ShouldBe(5);
        Read("-1").ShouldBe(5);
        Read("пять").ShouldBe(5);
        LoginLockout.ThresholdKey.ShouldBe("Auth:LockoutAfterFailedLogins");
    }

    private UnlockUserAccountCommand.Handler Handler() => new(_accounts, _administration);
}
