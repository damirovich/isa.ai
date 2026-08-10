using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

/// <summary>
/// Удалить тип документа (Администратор). ИНВАРИАНТ: запрещено, если по типу есть документы.
/// </summary>
/// <remarks>
/// Удаление нужно рядом с признаком активности, а не вместо него: неактивный тип остаётся
/// в справочнике и продолжает занимать имя, а ошибочно заведённый — просто мусор. Использованный
/// тип не удаляется никогда: у зарегистрированных документов пропала бы группа, а с ней и правила
/// их поведения (§3.1).
/// </remarks>
public sealed record DeleteDocumentTypeCommand(int Id)
    : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"document_type:delete:{Id}";

    /// <inheritdoc cref="DeleteDocumentTypeCommand" />
    public sealed class Handler(IDocumentTypeStore store, IDocFlowAdministration administration)
        : IRequestHandler<DeleteDocumentTypeCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(
            DeleteDocumentTypeCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Справочник — административный объект (§2.1 ТЗ): право даёт профиль через нейтральный
            // порт, модуль о ролях не знает (ADR-0017).
            if (!await administration.CanManageAsync(cancellationToken))
            {
                return ResponseDto<bool>.BadRequest(
                    "Удаление типов документов доступно только Администратору.");
            }

            var result = await store.DeleteAsync(command.Id, cancellationToken);
            return result switch
            {
                DocumentTypeWriteResult.Ok => ResponseDto<bool>.Ok(true, "Тип документа удалён."),
                DocumentTypeWriteResult.NotFound => ResponseDto<bool>.NotFound("Тип документа не найден."),
                _ => ResponseDto<bool>.BadRequest(
                    "Тип нельзя удалить: по нему уже зарегистрированы документы. "
                    + "Выведите его из обращения, сняв признак «действующий»."),
            };
        }
    }
}
