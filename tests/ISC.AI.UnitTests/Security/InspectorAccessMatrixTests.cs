using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector;
using ISC.AI.Profile.Inspector.Application.Features.Access;
using ISC.AI.Profile.Inspector.Application.Features.Divisions;
using ISC.AI.Profile.Inspector.Data;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Security;

/// <summary>
/// Матрица доступа «ИнспекторAI» (§2.1 ТЗ СКИД, ТБ-012, ADR-0033): умолчания повторяют прежние жёсткие правила
/// сервера; сохранённое отличие меняет ответ сервера и меню; замкнутые ячейки не меняются ни с экрана, ни записью в
/// БД в обход него; без субъекта — отказ до обращения к данным.
/// </summary>
public sealed class InspectorAccessMatrixTests
{
    private const int Me = 42;

    private static readonly UserRole[] AllRoles = Enum.GetValues<UserRole>();
    private static readonly string[] AllModuleIds = [.. new InspectorProfile().Modules.Select(m => m.Id)];
    private static readonly RolePermissionOverride[] NoOverrides = [];

    /// <summary>
    /// Прежние правила сервера до матрицы (ViolationGuard, MethodRegistryGuard, NormGuard, AdministrationRule,
    /// InspectorPlatformAdministration, DocFlowAdministration, InspectorAccessPolicy) — умолчания обязаны их повторять.
    /// </summary>
    private static readonly Dictionary<string, UserRole[]> LegacyRules = new()
    {
        [InspectorPermissions.ViolationsEdit] = [UserRole.Inspector, UserRole.Manager, UserRole.Administrator],
        [InspectorPermissions.NormsManage] = [UserRole.Administrator],
        [InspectorPermissions.MethodsSave] = [UserRole.Inspector, UserRole.Manager, UserRole.Administrator],
        [InspectorPermissions.MethodsManage] = [UserRole.Manager, UserRole.Administrator],
        [InspectorPermissions.DivisionsManage] = AllRoles,
        [InspectorPermissions.DocFlowView] = AllRoles,
        [InspectorPermissions.DocFlowSettings] = [UserRole.Administrator],
        // Новое право: прежде сервер снятие с контроля не проверял; по ТЗ СКИД §4.2 — только Руководитель.
        [InspectorPermissions.DocFlowClose] = [UserRole.Manager],
        [InspectorPermissions.AdminUsers] = [UserRole.Administrator],
        [InspectorPermissions.AdminMatrix] = [UserRole.Administrator],
        [InspectorPermissions.AdminDirectories] = [UserRole.Administrator],
        [InspectorPermissions.AdminAudit] = [UserRole.Administrator],
    };

    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public InspectorAccessMatrixTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Me);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _roles.ListPermissionOverridesAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact(DisplayName = "Перечень прав Инспектора: ключи уникальны, подписи есть, таблица прежних правил полная, меню ссылается на существующие права")]
    public void Catalog_is_consistent()
    {
        InspectorPermissions.All.Select(p => p.Key).ShouldBeUnique();
        InspectorPermissions.All.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Label) && !string.IsNullOrWhiteSpace(p.Hint));
        InspectorPermissions.All.Select(p => p.Key).Order().ShouldBe(LegacyRules.Keys.Order());
        InspectorPermissions.MenuSections.SelectMany(s => s.Permissions).ShouldAllBe(key => InspectorPermissions.Find(key) != null);
    }

    [Fact(DisplayName = "Каждый раздел Инспектора есть в таблице меню, лишних нет")]
    public void Every_module_has_menu_rule()
    {
        AllModuleIds.Where(id => !InspectorModuleVisibility.Rules.ContainsKey(id)).ShouldBeEmpty();
        InspectorModuleVisibility.Rules.Keys.Where(id => !AllModuleIds.Contains(id)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Без правок матрицы сервер Инспектора отвечает как прежние правила — по всем ролям и «без роли»")]
    public async Task Defaults_match_legacy_rules()
    {
        foreach (var (key, allowed) in LegacyRules)
        {
            foreach (var role in AllRoles)
            {
                _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(role);
                (await PermissionRule.CallerHasAsync(_roles, _subject, key)).ShouldBe(allowed.Contains(role), $"{key}, роль {role}");
            }

            _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns((UserRole?)null);
            (await PermissionRule.CallerHasAsync(_roles, _subject, key)).ShouldBeFalse($"{key}, без роли");
        }
    }

    [Fact(DisplayName = "Пока Администратора нет, без роли открыто всё, кроме документов (их фильтрует роль) и снятия поручений с контроля")]
    public async Task Initial_setup_opens_everything_but_documents()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns((UserRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        foreach (var key in LegacyRules.Keys)
        {
            (await PermissionRule.CallerHasAsync(_roles, _subject, key)).ShouldBe(key is not (InspectorPermissions.DocFlowView or InspectorPermissions.DocFlowClose), key);
        }
    }

    [Fact(DisplayName = "Без аутентификации — отказ, хранилище не спрашивается")]
    public async Task Anonymous_is_denied_before_any_lookup()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await PermissionRule.CallerHasAsync(_roles, _subject, InspectorPermissions.ViolationsEdit)).ShouldBeFalse();
        await _roles.DidNotReceiveWithAnyArgs().GetRoleAsync(default);
        await _roles.DidNotReceiveWithAnyArgs().GetPermissionOverrideAsync(default, default!);
    }

    [Fact(DisplayName = "Отличие меняет ответ сервера: Руководителю открыт журнал, Инспектору закрыты нарушения")]
    public async Task Override_changes_server_answer()
    {
        _roles.GetPermissionOverrideAsync(UserRole.Manager, InspectorPermissions.AdminAudit, Arg.Any<CancellationToken>()).Returns(true);
        _roles.GetPermissionOverrideAsync(UserRole.Inspector, InspectorPermissions.ViolationsEdit, Arg.Any<CancellationToken>()).Returns(false);

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(UserRole.Manager);
        (await new InspectorPlatformAdministration(_roles, _subject).CanViewAuditAsync()).ShouldBeTrue();
        (await new InspectorPlatformAdministration(_roles, _subject).CanManageAsync()).ShouldBeFalse();

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(UserRole.Inspector);
        (await PermissionRule.CallerHasAsync(_roles, _subject, InspectorPermissions.ViolationsEdit)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Запись в БД не меняет замкнутые ячейки; хранилище по ним не спрашивается")]
    public async Task Stored_value_never_overrides_lock()
    {
        _roles.GetPermissionOverrideAsync(Arg.Any<UserRole>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _roles.GetPermissionOverrideAsync(UserRole.Administrator, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        (await PermissionRule.IsGrantedAsync(_roles, UserRole.Manager, InspectorPermissions.AdminUsers)).ShouldBeFalse();
        (await PermissionRule.IsGrantedAsync(_roles, UserRole.Administrator, InspectorPermissions.AdminMatrix)).ShouldBeTrue();
        await _roles.DidNotReceiveWithAnyArgs().GetPermissionOverrideAsync(default, default!);
    }

    [Fact(DisplayName = "Подразделения: без права — отказ, справочник не трогается")]
    public async Task Divisions_require_permission()
    {
        var store = Substitute.For<IDivisionAdminStore>();
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(UserRole.Performer);
        _roles.GetPermissionOverrideAsync(UserRole.Performer, InspectorPermissions.DivisionsManage, Arg.Any<CancellationToken>()).Returns(false);

        var response = await new CreateDivisionCommand.Handler(store, _roles, _subject)
            .Handle(new CreateDivisionCommand("Отдел", null, null), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain("Подразделения");
        await store.DidNotReceiveWithAnyArgs().CreateAsync(default!, default, default, default, default);
    }

    [Fact(DisplayName = "Меню: Исполнитель не видит администрирование, но видит чат и базу НПА; отличие открывает раздел")]
    public void Menu_follows_matrix()
    {
        var performer = InspectorModuleVisibility.Visible(AllModuleIds, UserRole.Performer, initialSetup: false, NoOverrides);
        performer.ShouldContain("chat");
        performer.ShouldContain("npa-search");
        performer.ShouldContain("docflow-documents");
        performer.ShouldNotContain("admin-users");
        performer.ShouldNotContain("admin-access-matrix");
        performer.ShouldNotContain("admin-audit");

        InspectorModuleVisibility.Visible(AllModuleIds, UserRole.Administrator, initialSetup: false, NoOverrides).Count
            .ShouldBe(AllModuleIds.Length);

        RolePermissionOverride[] overrides = [new(UserRole.Manager, InspectorPermissions.AdminAudit, true)];
        InspectorModuleVisibility.Visible(AllModuleIds, UserRole.Manager, initialSetup: false, overrides).ShouldContain("admin-audit");

        var noRole = InspectorModuleVisibility.Visible(AllModuleIds, role: null, initialSetup: false, NoOverrides);
        noRole.ShouldContain("chat");                  // сервер ролью не ограничивает — меню не прячет
        noRole.ShouldNotContain("docflow-documents");  // документы без роли не выдаются
    }

    [Fact(DisplayName = "Экран матрицы Инспектора: только Администратор; сохранение замкнутой ячейки отклоняется целиком")]
    public async Task Matrix_scenarios_are_guarded()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(UserRole.Manager);
        (await new GetAccessMatrixQuery.Handler(_roles, _subject).Handle(new GetAccessMatrixQuery(), CancellationToken.None))
            .StatusMessage.ShouldBe(AccessMatrixText.Denied);

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(UserRole.Administrator);
        var view = (await new GetAccessMatrixQuery.Handler(_roles, _subject).Handle(new GetAccessMatrixQuery(), CancellationToken.None)).Data!;
        view.Rows.Count.ShouldBe(InspectorPermissions.All.Count);

        var locked = await new SaveAccessMatrixCommand.Handler(_roles, _subject).Handle(new SaveAccessMatrixCommand(
        [
            new(UserRole.Manager, InspectorPermissions.AdminAudit, true),
            new(UserRole.Manager, InspectorPermissions.AdminUsers, true),
        ]), CancellationToken.None);
        locked.StatusMessage.ShouldContain("не меняется");
        await _roles.DidNotReceiveWithAnyArgs().ApplyPermissionChangesAsync(default!, default);

        (await new SaveAccessMatrixCommand.Handler(_roles, _subject).Handle(new SaveAccessMatrixCommand(
            [new(UserRole.Manager, InspectorPermissions.AdminAudit, true)]), CancellationToken.None)).Status.ShouldBeTrue();
        await _roles.Received(1).ApplyPermissionChangesAsync(
            Arg.Is<IReadOnlyCollection<RolePermissionChange>>(c =>
                c.Single() == new RolePermissionChange(UserRole.Manager, InspectorPermissions.AdminAudit, true)),
            Me, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Описание роли Инспектора строится по действующей матрице")]
    public void Role_description_follows_matrix()
    {
        InspectorUserRoleCatalog.Describe(UserRole.Manager, []).ShouldNotContain("журнал аудита");
        InspectorUserRoleCatalog.Describe(UserRole.Manager, [new RolePermissionOverride(UserRole.Manager, InspectorPermissions.AdminAudit, true)])
            .ShouldContain("журнал аудита");
        InspectorUserRoleCatalog.Describe(UserRole.Performer, []).ShouldContain("поручение");
    }
}
