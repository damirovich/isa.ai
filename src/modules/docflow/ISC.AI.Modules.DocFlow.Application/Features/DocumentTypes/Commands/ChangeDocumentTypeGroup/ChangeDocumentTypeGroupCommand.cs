using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <summary>
/// Сменить группу типа (Администратор). ИНВАРИАНТ (§3.1): запрещено при наличии документов типа —
/// проверку выполняет хранилище (<see cref="DocumentTypeWriteResult.HasDocuments"/>).
/// </summary>
public sealed record ChangeDocumentTypeGroupCommand(int Id, DocumentGroup NewGroup)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"document_type:change-group:{Id}:{NewGroup}";

    /// <inheritdoc cref="ChangeDocumentTypeGroupCommand" />
    public sealed class Handler(IDocumentTypeStore store)
        : IRequestHandler<ChangeDocumentTypeGroupCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            ChangeDocumentTypeGroupCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var result = await store.ChangeGroupAsync(command.Id, command.NewGroup, cancellationToken);
            return result switch
            {
                DocumentTypeWriteResult.Ok => ResponseDto<bool>.Ok(true),
                DocumentTypeWriteResult.NotFound => ResponseDto<bool>.NotFound("Тип документа не найден."),
                _ => ResponseDto<bool>.BadRequest(
                    "Группу нельзя изменить: по этому типу уже зарегистрированы документы (ТЗ §3.1)."),
            };
        }
    }
}
