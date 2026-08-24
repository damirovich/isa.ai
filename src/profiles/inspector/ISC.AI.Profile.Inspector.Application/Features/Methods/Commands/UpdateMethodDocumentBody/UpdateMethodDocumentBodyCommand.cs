using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Править текст методики (человеком, без ИИ — ИИ-доработка это модуль «Редактор»).
/// Правка возвращает утверждённую методику в черновики: утверждение относится к редакции текста.
/// </summary>
public sealed record UpdateMethodDocumentBodyCommand(int MethodId, string Body)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:method:{MethodId}:edit";

    /// <inheritdoc cref="UpdateMethodDocumentBodyCommand" />
    public sealed class Handler(
        IMethodRegistryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider,
        IAccessContextProvider accessContextProvider)
        : IRequestHandler<UpdateMethodDocumentBodyCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateMethodDocumentBodyCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await MethodRegistryGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(MethodRegistryGuard.ManageDenied);
            }

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var result = await store.UpdateBodyAsync(
                command.MethodId, command.Body, access.MaxClassification, cancellationToken);
            return result == MethodWriteResult.Ok
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound("Методика не найдена.");
        }
    }
}

/// <summary>Пустой текст правкой не считается.</summary>
public sealed class UpdateMethodDocumentBodyValidator : AbstractValidator<UpdateMethodDocumentBodyCommand>
{
    /// <inheritdoc cref="UpdateMethodDocumentBodyValidator" />
    public UpdateMethodDocumentBodyValidator() =>
        RuleFor(c => c.Body).NotEmpty().WithMessage("Пустой текст сохранять не во что.");
}
