using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <summary>Изменить наименование/активность типа (Администратор, §3.1). Группа меняется отдельной командой.</summary>
public sealed record UpdateDocumentTypeCommand(int Id, string Name, bool IsActive)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"document_type:update:{Id}";

    /// <inheritdoc cref="UpdateDocumentTypeCommand" />
    public sealed class Handler(IDocumentTypeStore store) : IRequestHandler<UpdateDocumentTypeCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            UpdateDocumentTypeCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var result = await store.UpdateAsync(command.Id, command.Name.Trim(), command.IsActive, cancellationToken);
            return result switch
            {
                DocumentTypeWriteResult.Ok => ResponseDto<bool>.Ok(true),
                DocumentTypeWriteResult.NotFound => ResponseDto<bool>.NotFound("Тип документа не найден."),
                _ => ResponseDto<bool>.BadRequest("Тип с таким наименованием уже существует."),
            };
        }
    }
}
