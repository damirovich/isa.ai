using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.DocumentTypes;

/// <summary>
/// Сценарии справочника типов документов (ТЗ СКИД §3.1) — перенос из
/// <c>SKID.Application/Features/DocumentTypes</c> (калибровочный модуль Э4-35, этап 1).
/// </summary>
/// <remarks>
/// Отличия от исходника СКИД (образец для этапа 3):
/// MediatR → Mediator (те же имена контрактов, <c>Task</c> → <c>ValueTask</c>, генератор — в хосте);
/// <c>Result&lt;T&gt;</c> СКИД → <see cref="ResponseDto{T}"/>; ручной <c>IAuditService.LogAsync</c> в хендлерах →
/// сквозной <c>AuditBehavior</c> по маркеру <see cref="IAuditableRequest"/> (ТБ-030 — аудит нельзя «забыть»);
/// <c>[RequireRole(Admin)]</c> → политика модуля (по ТЗ §2.1 справочник ведёт Администратор;
/// ролевые политики — этап 6 Э4-35); доступ к данным — через порт домена, не напрямую в DbContext.
/// </remarks>

/// <summary>Список типов документов с необязательными фильтрами (§3.4).</summary>
public sealed record ListDocumentTypesQuery(DocumentGroup? Group = null, bool? IsActive = null)
    : IRequest<ResponseDto<IReadOnlyList<DocumentTypeItem>>>
{
    /// <inheritdoc cref="ListDocumentTypesQuery" />
    public sealed class Handler(IDocumentTypeStore store)
        : IRequestHandler<ListDocumentTypesQuery, ResponseDto<IReadOnlyList<DocumentTypeItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DocumentTypeItem>>> Handle(
            ListDocumentTypesQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var items = await store.ListAsync(query.Group, query.IsActive, cancellationToken);
            return ResponseDto<IReadOnlyList<DocumentTypeItem>>.Ok(items, items.Count);
        }
    }
}

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
