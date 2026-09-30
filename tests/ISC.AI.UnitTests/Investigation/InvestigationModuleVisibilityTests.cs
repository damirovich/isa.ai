using ISC.AI.Profile.Investigation;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Разделы меню «Следствия» по роли (ТП-004): роль видит только то, где сервер ей что-то разрешает; каждый модуль
/// профиля описан в таблице явно — новый раздел не может молча появиться у всех или пропасть у всех.
/// </summary>
public sealed class InvestigationModuleVisibilityTests
{
    private static readonly string[] AllIds = [.. new InvestigationProfile().Modules.Select(m => m.Id)];

    [Fact(DisplayName = "Каждый раздел профиля есть в таблице видимости")]
    public void Every_profile_module_has_rule()
    {
        AllIds.Where(id => !InvestigationModuleVisibility.Rules.ContainsKey(id)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Верификатор: дашборд, дела, верификация, документы и отчёты — без поиска по лицу и администрирования")]
    public void Verifier_sees_only_own_sections()
    {
        InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Verifier, canManage: false).Order()
            .ShouldBe(new[] { "cases", "dashboard", "docflow-documents", "docflow-reports", "media-verification" }.Order());
    }

    [Fact(DisplayName = "Администратор видит всё")]
    public void Administrator_sees_everything()
    {
        InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Administrator, canManage: true).Count.ShouldBe(AllIds.Length);
    }

    [Fact(DisplayName = "Следователь ищет по лицу, но не верифицирует и не администрирует")]
    public void Investigator_searches_but_does_not_verify()
    {
        var visible = InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Investigator, canManage: false);
        visible.ShouldContain("media-search");
        visible.ShouldNotContain("media-verification");
        visible.ShouldNotContain("admin-users");
        visible.ShouldNotContain("admin-audit");
    }

    [Fact(DisplayName = "Офицер ИБ видит журнал аудита, но не «Пользователей» и справочники")]
    public void Security_officer_sees_audit_only_in_admin()
    {
        var visible = InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.SecurityOfficer, canManage: false);
        visible.ShouldContain("admin-audit");
        visible.ShouldNotContain("admin-users");
        visible.ShouldNotContain("admin-divisions");
    }

    [Fact(DisplayName = "Без роли — ничего; пока Администратора нет (первичная настройка) — разделы администрирования видны")]
    public void No_role_sees_nothing_until_bootstrap()
    {
        InvestigationModuleVisibility.Visible(AllIds, role: null, canManage: false).ShouldBeEmpty();

        var bootstrap = InvestigationModuleVisibility.Visible(AllIds, role: null, canManage: true);
        bootstrap.ShouldContain("admin-divisions");
        bootstrap.ShouldContain("admin-users");
        bootstrap.ShouldNotContain("admin-report-permits"); // только настоящему Администратору
    }

    [Fact(DisplayName = "Раздел, которого нет в таблице, не прячется молча — виден любой роли")]
    public void Unknown_module_is_visible_to_any_role()
    {
        InvestigationModuleVisibility.Visible(["new-module"], InvestigationRole.Verifier, canManage: false).ShouldContain("new-module");
    }
}
