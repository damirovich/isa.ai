using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <summary>Вывести подразделение из обращения или вернуть в него.</summary>
/// <remarks>
/// Нужно ОТДЕЛЬНО от удаления: расформированное подразделение удалить нельзя — за ним числятся
/// документы и поручения, и их владелец превратился бы в число без имени. Неактивное перестаёт
/// предлагаться при регистрации и в назначениях, но история остаётся читаемой.
/// </remarks>
public sealed record SetDivisionActiveCommand(int Id, bool IsActive)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:{Id}:{(IsActive ? "enable" : "disable")}";

    /// <inheritdoc cref="SetDivisionActiveCommand" />
    public sealed class Handler(IDivisionAdminStore store)
        : IRequestHandler<SetDivisionActiveCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            SetDivisionActiveCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            return await store.SetActiveAsync(command.Id, command.IsActive, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Подразделение не найдено.");
        }
    }
}
