using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Profile.Investigation.Application.Features.Access;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Data;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using NSubstitute;
using Shouldly;

namespace ISC.AI.UnitTests.Investigation;

/// <summary>
/// Матрица доступа «Следствия» (ТП-004, ТБ-012, ADR-0032): умолчания повторяют прежние жёсткие правила сервера один в
/// один; сохранённое отличие меняет ответ сервера; замкнутые ячейки не меняются ни с экрана, ни записью в БД в обход
/// него; без субъекта — отказ до обращения к данным; режим первичной настройки открывает только администрирование.
/// </summary>
public sealed class AccessMatrixTests
{
    private const int Me = 42;

    private static readonly InvestigationRole[] AllRoles = Enum.GetValues<InvestigationRole>();

    /// <summary>
    /// Прежние правила сервера до матрицы (RoleGuard.CaseEditors, MediaAdministration, VerificationPolicy,
    /// AdministrationRule, CaseReports, PurgeCase, CaseAccessRule, InvestigationAccessPolicy) — умолчания обязаны их повторять.
    /// </summary>
    private static readonly Dictionary<string, InvestigationRole[]> LegacyRules = new()
    {
        [InvestigationPermissions.CasesView] = AllRoles,
        [InvestigationPermissions.CasesEdit] = [InvestigationRole.Investigator, InvestigationRole.Head, InvestigationRole.Administrator],
        [InvestigationPermissions.CasesPurge] = [InvestigationRole.Administrator],
        [InvestigationPermissions.MediaUpload] = [InvestigationRole.Investigator, InvestigationRole.Administrator],
        [InvestigationPermissions.MediaSearch] = [InvestigationRole.Investigator, InvestigationRole.FaceExpert, InvestigationRole.Administrator],
        [InvestigationPermissions.MediaPurge] = [InvestigationRole.Administrator, InvestigationRole.Head],
        [InvestigationPermissions.VerificationExpert] = [InvestigationRole.FaceExpert, InvestigationRole.Administrator],
        [InvestigationPermissions.VerificationVerifier] = [InvestigationRole.Verifier, InvestigationRole.Administrator],
        [InvestigationPermissions.DocFlowView] = AllRoles,
        [InvestigationPermissions.DocFlowSettings] = [InvestigationRole.Administrator],
        [InvestigationPermissions.ReportPermits] = [InvestigationRole.Administrator],
        [InvestigationPermissions.AdminUsers] = [InvestigationRole.Administrator],
        [InvestigationPermissions.AdminMatrix] = [InvestigationRole.Administrator],
        [InvestigationPermissions.AdminDirectories] = [InvestigationRole.Administrator],
        [InvestigationPermissions.AdminAudit] = [InvestigationRole.Administrator, InvestigationRole.SecurityOfficer],
    };

    /// <summary>Права, которые прежде открывались любому вошедшему, пока Администратора нет.</summary>
    private static readonly string[] LegacyInitialSetup =
    [
        InvestigationPermissions.DocFlowSettings, InvestigationPermissions.AdminUsers, InvestigationPermissions.AdminMatrix,
        InvestigationPermissions.AdminDirectories, InvestigationPermissions.AdminAudit,
    ];

    private readonly IUserRoleStore _roles = Substitute.For<IUserRoleStore>();
    private readonly ISubjectProvider _subject = Substitute.For<ISubjectProvider>();

    public AccessMatrixTests()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)Me);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(true);
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns([]);
        _roles.ListPermissionOverridesAsync(Arg.Any<CancellationToken>()).Returns([]);
    }

    [Fact(DisplayName = "Перечень прав: ключи уникальны, у каждой строки есть подпись и пояснение, таблица прежних правил полная")]
    public void Catalog_is_consistent()
    {
        InvestigationPermissions.All.Select(p => p.Key).ShouldBeUnique();
        InvestigationPermissions.All.ShouldAllBe(p => !string.IsNullOrWhiteSpace(p.Label) && !string.IsNullOrWhiteSpace(p.Hint));
        InvestigationPermissions.All.Select(p => p.Key).Order().ShouldBe(LegacyRules.Keys.Order());
        InvestigationPermissions.MenuSections.SelectMany(s => s.Permissions)
            .ShouldAllBe(key => InvestigationPermissions.Find(key) != null);
    }

    [Fact(DisplayName = "Замкнутые строки: «Пользователи», «Матрица доступа», «Уничтожение дела» — только Администратор, с причиной в каждой ячейке")]
    public void Locked_rows_belong_to_administrator()
    {
        var locked = InvestigationPermissions.All.Where(p => p.AdministratorOnlyReason is not null).Select(p => p.Key).Order();
        locked.ShouldBe(new[] { InvestigationPermissions.AdminMatrix, InvestigationPermissions.AdminUsers, InvestigationPermissions.CasesPurge }.Order());

        foreach (var permission in InvestigationPermissions.All.Where(p => p.AdministratorOnlyReason is not null))
        {
            permission.Defaults.ShouldBe(new[] { InvestigationRole.Administrator });
            AllRoles.ShouldAllBe(role => InvestigationPermissions.LockReason(permission, role) != null);
        }
    }

    [Fact(DisplayName = "Без правок матрицы сервер отвечает ровно как прежние жёсткие правила — по всем ролям и «без роли»")]
    public async Task Defaults_match_legacy_rules()
    {
        foreach (var (key, allowed) in LegacyRules)
        {
            foreach (var role in AllRoles)
            {
                _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(role);
                (await PermissionRule.CallerHasAsync(_roles, _subject, key))
                    .ShouldBe(allowed.Contains(role), $"{key}, роль {role}");
            }

            _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);
            (await PermissionRule.CallerHasAsync(_roles, _subject, key)).ShouldBeFalse($"{key}, без роли");
        }
    }

    [Fact(DisplayName = "Пока Администратора нет, без роли открыто только администрирование и настройки документооборота")]
    public async Task Initial_setup_opens_only_administration()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns((InvestigationRole?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        foreach (var key in LegacyRules.Keys)
        {
            (await PermissionRule.CallerHasAsync(_roles, _subject, key)).ShouldBe(LegacyInitialSetup.Contains(key), key);
        }
    }

    [Fact(DisplayName = "Без аутентификации — отказ, хранилище ролей и матрицы не спрашивается")]
    public async Task Anonymous_is_denied_before_any_lookup()
    {
        _subject.GetCurrentUserIdAsync(Arg.Any<CancellationToken>()).Returns((int?)null);
        _roles.AnyAdministratorAsync(Arg.Any<CancellationToken>()).Returns(false);

        (await PermissionRule.CallerHasAsync(_roles, _subject, InvestigationPermissions.AdminUsers)).ShouldBeFalse();
        await _roles.DidNotReceiveWithAnyArgs().GetRoleAsync(default);
        await _roles.DidNotReceiveWithAnyArgs().GetPermissionOverrideAsync(default, default!);
    }

    [Fact(DisplayName = "Сохранённое отличие меняет ответ сервера: Верификатору открыт поиск, Следователю закрыта загрузка")]
    public async Task Override_changes_server_answer()
    {
        _roles.GetPermissionOverrideAsync(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, Arg.Any<CancellationToken>())
            .Returns(true);
        _roles.GetPermissionOverrideAsync(InvestigationRole.Investigator, InvestigationPermissions.MediaUpload, Arg.Any<CancellationToken>())
            .Returns(false);

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);
        (await new MediaAdministration(_roles, _subject).CanSearchAsync()).ShouldBeTrue();

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Investigator);
        (await new MediaAdministration(_roles, _subject).CanUploadAsync()).ShouldBeFalse();
        (await new MediaAdministration(_roles, _subject).CanSearchAsync()).ShouldBeTrue();
    }

    [Fact(DisplayName = "Стадии верификации идут по матрице: закрытая Эксперту первая подпись — отказ")]
    public async Task Verification_follows_matrix()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.FaceExpert);
        _roles.GetPermissionOverrideAsync(InvestigationRole.FaceExpert, InvestigationPermissions.VerificationExpert, Arg.Any<CancellationToken>())
            .Returns(false);

        (await new VerificationPolicy(_roles).CanActAsync(VerificationStage.Expert, Me)).ShouldBeFalse();
        (await new VerificationPolicy(_roles).CanActAsync(VerificationStage.Verifier, Me)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Запись в БД не открывает и не закрывает замкнутую ячейку; хранилище по ней даже не спрашивается")]
    public async Task Stored_value_never_overrides_lock()
    {
        _roles.GetPermissionOverrideAsync(Arg.Any<InvestigationRole>(), Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(true);
        _roles.GetPermissionOverrideAsync(InvestigationRole.Administrator, Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(false);

        (await PermissionRule.IsGrantedAsync(_roles, InvestigationRole.Head, InvestigationPermissions.AdminUsers)).ShouldBeFalse();
        (await PermissionRule.IsGrantedAsync(_roles, InvestigationRole.Investigator, InvestigationPermissions.CasesPurge)).ShouldBeFalse();
        (await PermissionRule.IsGrantedAsync(_roles, InvestigationRole.Administrator, InvestigationPermissions.AdminMatrix)).ShouldBeTrue();
        await _roles.DidNotReceiveWithAnyArgs().GetPermissionOverrideAsync(default, default!);
    }

    [Fact(DisplayName = "Закрытый реестр дел: роль для правила видимости дел — нет, дела и материалы не выдаются")]
    public async Task Closed_case_view_hides_cases()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Verifier);
        (await PermissionRule.ResolveCaseViewerAsync(_roles, Me)).ShouldBe(InvestigationRole.Verifier);

        _roles.GetPermissionOverrideAsync(InvestigationRole.Verifier, InvestigationPermissions.CasesView, Arg.Any<CancellationToken>())
            .Returns(false);
        (await PermissionRule.ResolveCaseViewerAsync(_roles, Me)).ShouldBeNull();
        (await PermissionRule.ResolveCaseViewerAsync(_roles, null)).ShouldBeNull();
    }

    [Fact(DisplayName = "Экран матрицы: не-Администратору — отказ; Администратору — ячейки с учётом отличий, число отличий без мусора")]
    public async Task Get_matrix_is_admin_only_and_reflects_overrides()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Head);
        var denied = await new GetAccessMatrixQuery.Handler(_roles, _subject).Handle(new GetAccessMatrixQuery(), CancellationToken.None);
        denied.Status.ShouldBeFalse();
        denied.StatusMessage.ShouldBe(RoleGuard.AdminDenied);
        await _roles.DidNotReceive().ListPermissionOverridesAsync(Arg.Any<CancellationToken>());

        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);
        _roles.ListPermissionOverridesAsync(Arg.Any<CancellationToken>()).Returns(
        [
            new(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true, Me, new DateTime(2026, 9, 30, 8, 0, 0, DateTimeKind.Utc)),
            new(InvestigationRole.Head, InvestigationPermissions.AdminUsers, true),   // замкнутая ячейка — не считается
            new(InvestigationRole.Head, "removed.permission", true),                  // неизвестный ключ — не считается
        ]);
        _roles.ListAsync(Arg.Any<CancellationToken>()).Returns(
            [new UserRoleRow(Me, "Администратор", InvestigationRole.Administrator), new UserRoleRow(7, "Ашыров", InvestigationRole.Verifier)]);

        var view = (await new GetAccessMatrixQuery.Handler(_roles, _subject).Handle(new GetAccessMatrixQuery(), CancellationToken.None)).Data!;

        view.OverrideCount.ShouldBe(1);
        view.LastChangedBy.ShouldBe("Администратор");
        view.Roles.Single(r => r.Role == InvestigationRole.Verifier).UserCount.ShouldBe(1);
        Cell(view, InvestigationPermissions.MediaSearch, InvestigationRole.Verifier).IsGranted.ShouldBeTrue();
        Cell(view, InvestigationPermissions.MediaSearch, InvestigationRole.Verifier).IsDefault.ShouldBeFalse();
        Cell(view, InvestigationPermissions.AdminUsers, InvestigationRole.Head).IsGranted.ShouldBeFalse();
        Cell(view, InvestigationPermissions.AdminUsers, InvestigationRole.Head).LockReason.ShouldNotBeNull();
        Cell(view, InvestigationPermissions.CasesEdit, InvestigationRole.Head).LockReason.ShouldBeNull();
    }

    [Fact(DisplayName = "Сохранение: отличие от умолчания пишется значением, совпадение с умолчанием — удаляет строку")]
    public async Task Save_writes_differences_and_clears_defaults()
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(InvestigationRole.Administrator);

        var response = await new SaveAccessMatrixCommand.Handler(_roles, _subject).Handle(new SaveAccessMatrixCommand(
        [
            new(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true),     // по умолчанию закрыто
            new(InvestigationRole.Investigator, InvestigationPermissions.MediaSearch, true), // совпадает с умолчанием
            new(InvestigationRole.Head, InvestigationPermissions.CasesEdit, false),         // по умолчанию открыто
        ]), CancellationToken.None);

        response.Status.ShouldBeTrue();
        await _roles.Received(1).ApplyPermissionChangesAsync(
            Arg.Is<IReadOnlyCollection<RolePermissionChange>>(c =>
                c.Count == 3
                && c.Contains(new RolePermissionChange(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true))
                && c.Contains(new RolePermissionChange(InvestigationRole.Investigator, InvestigationPermissions.MediaSearch, null))
                && c.Contains(new RolePermissionChange(InvestigationRole.Head, InvestigationPermissions.CasesEdit, false))),
            Me,
            Arg.Any<CancellationToken>());
    }

    [Theory(DisplayName = "Сохранение отклоняется целиком: замкнутая ячейка, неизвестное право, не-Администратор")]
    [InlineData(InvestigationRole.Administrator, InvestigationRole.Head, InvestigationPermissions.AdminUsers, "не меняется")]
    [InlineData(InvestigationRole.Administrator, InvestigationRole.Administrator, InvestigationPermissions.AdminMatrix, "не меняется")]
    [InlineData(InvestigationRole.Administrator, InvestigationRole.Investigator, InvestigationPermissions.CasesPurge, "не меняется")]
    [InlineData(InvestigationRole.Administrator, InvestigationRole.Head, "removed.permission", "Неизвестное право")]
    [InlineData(InvestigationRole.Head, InvestigationRole.Head, InvestigationPermissions.MediaSearch, "Администратору")]
    public async Task Save_rejects_forbidden_changes(InvestigationRole caller, InvestigationRole role, string permission, string message)
    {
        _roles.GetRoleAsync(Me, Arg.Any<CancellationToken>()).Returns(caller);

        var response = await new SaveAccessMatrixCommand.Handler(_roles, _subject).Handle(new SaveAccessMatrixCommand(
        [
            new(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true),
            new(role, permission, true),
        ]), CancellationToken.None);

        response.StatusCode.ShouldBe(ResponseStatusCode.BadRequest);
        response.StatusMessage.ShouldContain(message);
        await _roles.DidNotReceiveWithAnyArgs().ApplyPermissionChangesAsync(default!, default);
    }

    [Fact(DisplayName = "Журнал: каждая ячейка с новым значением; валидатор отклоняет пустой набор и повтор ячейки")]
    public void Audit_summary_and_validator()
    {
        var command = new SaveAccessMatrixCommand(
        [
            new(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true),
            new(InvestigationRole.Head, InvestigationPermissions.CasesEdit, false),
        ]);
        command.AuditSummary.ShouldBe("investigation:access-matrix:save:Verifier/media.search=on;Head/cases.edit=off");

        var validator = new SaveAccessMatrixValidator();
        validator.Validate(command).IsValid.ShouldBeTrue();
        validator.Validate(new SaveAccessMatrixCommand([])).IsValid.ShouldBeFalse();
        validator.Validate(new SaveAccessMatrixCommand(
        [
            new(InvestigationRole.Head, InvestigationPermissions.CasesEdit, false),
            new(InvestigationRole.Head, InvestigationPermissions.CasesEdit, true),
        ])).IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Описание роли в карточке сотрудника строится по действующей матрице")]
    public void Role_description_follows_matrix()
    {
        InvestigationUserRoleCatalog.Describe(InvestigationRole.Verifier, []).ShouldNotContain("поиск по лицу");

        var changed = InvestigationUserRoleCatalog.Describe(InvestigationRole.Verifier,
            [new RolePermissionOverride(InvestigationRole.Verifier, InvestigationPermissions.MediaSearch, true)]);
        changed.ShouldContain("поиск по лицу");
        changed.ShouldContain("верификация — вторая подпись");

        InvestigationUserRoleCatalog.Describe(InvestigationRole.Investigator, []).ShouldContain("только свои");
    }

    private static AccessMatrixCell Cell(AccessMatrixView view, string permission, InvestigationRole role) =>
        view.Rows.Single(r => r.Key == permission).Cells.Single(c => c.Role == role);
}
