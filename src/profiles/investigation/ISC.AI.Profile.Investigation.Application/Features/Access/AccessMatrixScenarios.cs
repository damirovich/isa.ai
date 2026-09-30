using System.Globalization;
using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Access;

/// <summary>Столбец матрицы: роль и сколько сотрудников её сейчас носят.</summary>
/// <param name="Role">Роль.</param>
/// <param name="Label">Подпись роли.</param>
/// <param name="UserCount">Действующих сотрудников с этой ролью.</param>
public sealed record AccessMatrixRole(InvestigationRole Role, string Label, int UserCount);

/// <summary>Ячейка матрицы.</summary>
/// <param name="Role">Роль.</param>
/// <param name="IsGranted">Открыто ли право сейчас (итог: сохранённое значение или умолчание).</param>
/// <param name="IsDefault">Открыто ли право по умолчанию поставки.</param>
/// <param name="LockReason">Почему ячейку нельзя изменить; можно — <see langword="null"/>.</param>
public sealed record AccessMatrixCell(InvestigationRole Role, bool IsGranted, bool IsDefault, string? LockReason);

/// <summary>Строка матрицы — право с ячейками по всем ролям.</summary>
/// <param name="Key">Ключ права.</param>
/// <param name="Section">Группа на экране.</param>
/// <param name="Label">Подпись.</param>
/// <param name="Hint">Пояснение.</param>
/// <param name="Cells">Ячейки в порядке <see cref="AccessMatrixView.Roles"/>.</param>
public sealed record AccessMatrixRow(
    string Key, PermissionSection Section, string Label, string Hint, IReadOnlyList<AccessMatrixCell> Cells);

/// <summary>Матрица доступа для экрана.</summary>
/// <param name="Roles">Столбцы.</param>
/// <param name="Rows">Строки в порядке показа.</param>
/// <param name="OverrideCount">Сколько ячеек отличается от умолчаний.</param>
/// <param name="LastChangedAtUtc">Когда последний раз менялась ячейка, отличная от умолчания.</param>
/// <param name="LastChangedBy">Кто её менял (имя), если известно.</param>
/// <param name="IsInitialSetup">В контуре нет ни одного Администратора (режим первичной настройки).</param>
public sealed record AccessMatrixView(
    IReadOnlyList<AccessMatrixRole> Roles,
    IReadOnlyList<AccessMatrixRow> Rows,
    int OverrideCount,
    DateTime? LastChangedAtUtc,
    string? LastChangedBy,
    bool IsInitialSetup);

/// <summary>Новое значение ячейки.</summary>
/// <param name="Role">Роль.</param>
/// <param name="Permission">Ключ права.</param>
/// <param name="IsGranted">Открыть или закрыть.</param>
public sealed record AccessMatrixCellChange(InvestigationRole Role, string Permission, bool IsGranted);

/// <summary>Подписи групп матрицы.</summary>
public static class PermissionSectionLabels
{
    /// <summary>Подпись группы.</summary>
    public static string Label(this PermissionSection section) => section switch
    {
        PermissionSection.Cases => "Дела",
        PermissionSection.Media => "Медиа",
        PermissionSection.DocFlow => "Документооборот и сводки",
        PermissionSection.Administration => "Администрирование",
        _ => section.ToString(),
    };
}

/// <summary>
/// Матрица доступа профиля «Следствие» для экрана (ADR-0032): права × роли с итоговыми значениями, умолчаниями и
/// замками. Право — «Матрица доступа» (закреплено за Администратором; пока Администратора нет — любому вошедшему).
/// </summary>
public sealed record GetAccessMatrixQuery : IRequest<ResponseDto<AccessMatrixView>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "investigation:access-matrix:view";

    /// <inheritdoc cref="GetAccessMatrixQuery" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<GetAccessMatrixQuery, ResponseDto<AccessMatrixView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessMatrixView>> Handle(GetAccessMatrixQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // ТБ-012: право — до любого чтения.
            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.AdminMatrix, cancellationToken))
            {
                return ResponseDto<AccessMatrixView>.BadRequest(RoleGuard.AdminDenied);
            }

            var overrides = await roles.ListPermissionOverridesAsync(cancellationToken) ?? [];
            var users = await roles.ListAsync(cancellationToken);
            var allRoles = Enum.GetValues<InvestigationRole>();

            var columns = allRoles
                .Select(role => new AccessMatrixRole(role, role.Label(), users.Count(u => u.Role == role)))
                .ToList();

            var rows = InvestigationPermissions.All
                .Select(p => new AccessMatrixRow(
                    p.Key,
                    p.Section,
                    p.Label,
                    p.Hint,
                    [.. allRoles.Select(role => new AccessMatrixCell(
                        role,
                        InvestigationPermissions.IsGranted(p, role, overrides),
                        p.Defaults.Contains(role),
                        InvestigationPermissions.LockReason(p, role)))]))
                .ToList();

            // Считаются только действующие отличия: строки по неизвестным ключам или замкнутым ячейкам ничего не меняют.
            var effective = overrides
                .Where(o => InvestigationPermissions.Find(o.Permission) is { } p
                    && InvestigationPermissions.LockReason(p, o.Role) is null
                    && o.IsGranted != p.Defaults.Contains(o.Role))
                .ToList();

            var last = effective.OrderByDescending(o => o.UpdatedAtUtc).FirstOrDefault();
            var lastBy = last?.UpdatedByUserId is { } byId
                ? users.FirstOrDefault(u => u.UserId == byId)?.DisplayName ?? $"пользователь № {byId.ToString(CultureInfo.InvariantCulture)}"
                : null;

            return ResponseDto<AccessMatrixView>.Ok(new AccessMatrixView(
                columns,
                rows,
                effective.Count,
                last?.UpdatedAtUtc,
                lastBy,
                !await roles.AnyAdministratorAsync(cancellationToken)));
        }
    }
}

/// <summary>
/// Сохранить изменения матрицы доступа (ADR-0032): набор ячеек, которые Администратор переключил. Замкнутые ячейки и
/// неизвестные права отклоняются целиком — матрица не применяется «наполовину». В журнал пишется каждая ячейка с
/// новым значением (ТБ-030). Значение, совпавшее с умолчанием, удаляет сохранённое отличие — так же устроен и сброс
/// к умолчаниям: экран присылает умолчания как обычные изменения, и Администратор видит их список до сохранения.
/// </summary>
/// <param name="Changes">Переключённые ячейки.</param>
public sealed record SaveAccessMatrixCommand(IReadOnlyList<AccessMatrixCellChange> Changes)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Состав — «роль / право = открыто|закрыто» по каждой ячейке: что именно стало, видно из журнала без экрана.</remarks>
    public string? AuditSummary => "investigation:access-matrix:save:" + string.Join(";",
        (Changes ?? []).Select(c => $"{c.Role}/{c.Permission}={(c.IsGranted ? "on" : "off")}"));

    /// <inheritdoc cref="SaveAccessMatrixCommand" />
    public sealed class Handler(IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SaveAccessMatrixCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(SaveAccessMatrixCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.AdminMatrix, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.AdminDenied);
            }

            var requested = command.Changes ?? [];
            var changes = new List<RolePermissionChange>(requested.Count);
            foreach (var change in requested)
            {
                if (InvestigationPermissions.Find(change.Permission) is not { } permission || !Enum.IsDefined(change.Role))
                {
                    return ResponseDto<int>.BadRequest("Неизвестное право или роль — матрица не сохранена.");
                }

                // ИНВАРИАНТ (ADR-0032): замкнутая ячейка не меняется ни с экрана, ни запросом в обход него.
                if (InvestigationPermissions.LockReason(permission, change.Role) is { } locked)
                {
                    return ResponseDto<int>.BadRequest($"«{permission.Label}» для роли «{change.Role.Label()}» не меняется: {locked}");
                }

                // Совпало с умолчанием — строку удаляем: ячейка снова следует правилам поставки.
                var isDefault = permission.Defaults.Contains(change.Role) == change.IsGranted;
                changes.Add(new RolePermissionChange(change.Role, permission.Key, isDefault ? null : change.IsGranted));
            }

            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            await roles.ApplyPermissionChangesAsync(changes, userId, cancellationToken);
            return ResponseDto<int>.Ok(changes.Count);
        }
    }
}

/// <summary>Правила сохранения матрицы.</summary>
public sealed class SaveAccessMatrixValidator : AbstractValidator<SaveAccessMatrixCommand>
{
    /// <summary>Максимум ячеек в одном сохранении — вся матрица.</summary>
    public const int MaxChanges = 200;

    /// <summary>Хотя бы одно изменение, без повторов одной ячейки.</summary>
    public SaveAccessMatrixValidator()
    {
        RuleFor(c => c.Changes).NotNull().NotEmpty().WithMessage("Нет изменений для сохранения.");
        RuleFor(c => c.Changes.Count).LessThanOrEqualTo(MaxChanges).When(c => c.Changes is not null);
        RuleFor(c => c.Changes)
            .Must(changes => changes.Select(c => (c.Role, c.Permission)).Distinct().Count() == changes.Count)
            .When(c => c.Changes is not null)
            .WithMessage("Одна и та же ячейка указана дважды.");
    }
}
