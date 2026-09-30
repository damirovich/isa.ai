using ISC.AI.Profile.Investigation;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Разделы меню «Следствия» по матрице доступа (ТП-004, ADR-0032): роль видит только то, где сервер ей что-то
/// разрешает; каждый модуль профиля описан в таблице явно — новый раздел не может молча появиться у всех или пропасть у
/// всех. Меню считает по той же таблице, что и сервер: правка матрицы меняет и меню.
/// </summary>
public sealed class InvestigationModuleVisibilityTests
{
    private static readonly string[] AllIds = [.. new InvestigationProfile().Modules.Select(m => m.Id)];
    private static readonly RolePermissionOverride[] NoOverrides = [];

    [Fact(DisplayName = "Каждый раздел профиля есть в таблице видимости, и в таблице нет лишних разделов")]
    public void Every_profile_module_has_rule()
    {
        AllIds.Where(id => !InvestigationModuleVisibility.Rules.ContainsKey(id)).ShouldBeEmpty();
        InvestigationModuleVisibility.Rules.Keys.Where(id => !AllIds.Contains(id)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Верификатор: дашборд, дела, верификация, документы и отчёты — без поиска по лицу и администрирования")]
    public void Verifier_sees_only_own_sections()
    {
        Visible(InvestigationRole.Verifier).Order()
            .ShouldBe(new[] { "cases", "dashboard", "docflow-documents", "docflow-reports", "media-verification" }.Order());
    }

    [Fact(DisplayName = "Администратор видит всё")]
    public void Administrator_sees_everything()
    {
        Visible(InvestigationRole.Administrator).Count.ShouldBe(AllIds.Length);
    }

    [Fact(DisplayName = "Следователь ищет по лицу, но не верифицирует и не администрирует")]
    public void Investigator_searches_but_does_not_verify()
    {
        var visible = Visible(InvestigationRole.Investigator);
        visible.ShouldContain("media-search");
        visible.ShouldNotContain("media-verification");
        visible.ShouldNotContain("admin-users");
        visible.ShouldNotContain("admin-audit");
        visible.ShouldNotContain("admin-access-matrix");
    }

    [Fact(DisplayName = "Офицер ИБ видит журнал аудита, но не «Пользователей», матрицу и справочники")]
    public void Security_officer_sees_audit_only_in_admin()
    {
        var visible = Visible(InvestigationRole.SecurityOfficer);
        visible.ShouldContain("admin-audit");
        visible.ShouldNotContain("admin-users");
        visible.ShouldNotContain("admin-access-matrix");
        visible.ShouldNotContain("admin-divisions");
    }

    [Fact(DisplayName = "Без роли — ничего; пока Администратора нет (первичная настройка) — разделы администрирования видны")]
    public void No_role_sees_nothing_until_bootstrap()
    {
        InvestigationModuleVisibility.Visible(AllIds, role: null, initialSetup: false, NoOverrides).ShouldBeEmpty();

        var bootstrap = InvestigationModuleVisibility.Visible(AllIds, role: null, initialSetup: true, NoOverrides);
        bootstrap.ShouldContain("admin-divisions");
        bootstrap.ShouldContain("admin-users");
        bootstrap.ShouldContain("admin-access-matrix");
        bootstrap.ShouldNotContain("admin-report-permits"); // только настоящему Администратору
        bootstrap.ShouldNotContain("cases");                // делами без роли не работают даже на чистом контуре
    }

    [Fact(DisplayName = "Раздел, которого нет в таблице, не прячется молча — виден любой роли")]
    public void Unknown_module_is_visible_to_any_role()
    {
        InvestigationModuleVisibility.Visible(["new-module"], InvestigationRole.Verifier, initialSetup: false, NoOverrides)
            .ShouldContain("new-module");
    }

    [Fact(DisplayName = "Администратор открыл Верификатору поиск по лицу — раздел появился в меню Верификатора")]
    public void Granted_override_shows_section()
    {
        RolePermissionOverride[] overrides = [new(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true)];

        InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Verifier, initialSetup: false, overrides)
            .ShouldContain("media-search");
    }

    [Fact(DisplayName = "Администратор закрыл Следователю реестр дел — дашборд и дела пропали из его меню")]
    public void Revoked_override_hides_section()
    {
        RolePermissionOverride[] overrides = [new(InvestigationRole.Investigator, InvestigationPermissions.CasesView, false)];

        var visible = InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Investigator, initialSetup: false, overrides);
        visible.ShouldNotContain("cases");
        visible.ShouldNotContain("dashboard");
        visible.ShouldContain("media-search");
    }

    [Fact(DisplayName = "Строка в БД не открывает замкнутый раздел: «Пользователи» Руководителю не выдаются даже записью в обход экрана")]
    public void Locked_override_is_ignored()
    {
        RolePermissionOverride[] overrides =
        [
            new(InvestigationRole.Head, InvestigationPermissions.AdminUsers, true),
            new(InvestigationRole.Administrator, InvestigationPermissions.AdminMatrix, false),
        ];

        InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Head, initialSetup: false, overrides).ShouldNotContain("admin-users");
        InvestigationModuleVisibility.Visible(AllIds, InvestigationRole.Administrator, initialSetup: false, overrides)
            .ShouldContain("admin-access-matrix");
    }

    private static IReadOnlySet<string> Visible(InvestigationRole role) =>
        InvestigationModuleVisibility.Visible(AllIds, role, initialSetup: false, NoOverrides);
}
