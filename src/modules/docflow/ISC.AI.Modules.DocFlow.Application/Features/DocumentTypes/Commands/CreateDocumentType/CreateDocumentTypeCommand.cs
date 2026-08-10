using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <summary>Создать тип документа (Администратор, §3.1). Группа выбирается при создании.</summary>
public sealed record CreateDocumentTypeCommand(string Name, DocumentGroup Group, bool IsActive = true)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"document_type:create:{Name}";

    /// <inheritdoc cref="CreateDocumentTypeCommand" />
    public sealed class Handler(IDocumentTypeStore store) : IRequestHandler<CreateDocumentTypeCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            CreateDocumentTypeCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var id = await store.CreateAsync(command.Name.Trim(), command.Group, command.IsActive, cancellationToken);
            return id is { } created
                ? ResponseDto<int>.Ok(created)
                : ResponseDto<int>.BadRequest("Тип с таким наименованием уже существует.");
        }
    }
}
