using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;

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
