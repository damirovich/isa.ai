using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Divisions;

/// <summary>
/// Удалить подразделение. ИНВАРИАНТ: только если за ним ничего не числится и нет дочерних.
/// </summary>
public sealed record DeleteDivisionCommand(int Id) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:division:delete:{Id}";

    /// <inheritdoc cref="DeleteDivisionCommand" />
    public sealed class Handler(IDivisionAdminStore store)
        : IRequestHandler<DeleteDivisionCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteDivisionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            return await store.DeleteAsync(command.Id, cancellationToken) switch
            {
                DivisionWriteResult.Ok => ResponseDto<bool>.Ok(true, "Подразделение удалено."),
                DivisionWriteResult.NotFound => ResponseDto<bool>.NotFound("Подразделение не найдено."),
                _ => ResponseDto<bool>.BadRequest(
                    "Подразделение нельзя удалить: за ним числятся пользователи, документы или "
                    + "поручения либо у него есть дочерние. Выведите его из обращения, сняв признак "
                    + "«действующее» — история при этом сохранится."),
            };
        }
    }
}
