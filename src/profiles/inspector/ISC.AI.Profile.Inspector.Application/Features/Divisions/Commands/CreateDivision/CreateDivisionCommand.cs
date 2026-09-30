using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <summary>Создать подразделение (корневое либо дочернее к <paramref name="ParentId"/>).</summary>
/// <param name="Kind">Тип: территориальное/линейное (§4.2; разрез аналитики ТФ-АРХ-03).</param>
public sealed record CreateDivisionCommand(
    string Name, string? Code, int? ParentId,
    Domain.Enums.DivisionKind Kind = Domain.Enums.DivisionKind.Territorial)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:create:{Name}";

    /// <inheritdoc cref="CreateDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store, IUserRoleStore roles, ISubjectProvider subjectProvider) : IRequestHandler<CreateDivisionCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            CreateDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Право «Подразделения: ведение» матрицы доступа (ADR-0033) — до любого обращения к справочнику (ТБ-012).
            if (!await PermissionRule.CallerHasAsync(roles, subjectProvider, InspectorPermissions.DivisionsManage, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(PermissionRule.Denied(InspectorPermissions.DivisionsManage));
            }
            var id = await store.CreateAsync(
                command.Name, command.Code, command.ParentId, command.Kind, cancellationToken);
            return ResponseDto<int>.Ok(id);
        }
    }
}
