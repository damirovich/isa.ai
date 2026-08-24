using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Archive;

/// <summary>
/// Страница архива проверок (§5.2.4, ТФ-АРХ-01/02): группы «справка-проверка × подразделение»
/// из учёта нарушений, обогащённые метаданными документооборота. Справка, НЕДОСТУПНАЯ субъекту
/// по решётке (ТБ-020/021), остаётся голым номером — метаданные не подмешиваются.
/// </summary>
public sealed record ListInspectionArchiveQuery(
    int? DivisionId = null,
    int? CategoryId = null,
    ViolationSeverity? Severity = null,
    RemediationStatus? RemediationStatus = null,
    DateOnly? DetectedFrom = null,
    DateOnly? DetectedTo = null,
    string? RefSearch = null,
    int Page = 1,
    int PageSize = 20) : IRequest<ResponseDto<ArchiveResult>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:archive:list:division={DivisionId};category={CategoryId};ref={RefSearch};page={Page}";

    /// <inheritdoc cref="ListInspectionArchiveQuery" />
    public sealed class Handler(IInspectionArchiveStore store, IInspectionDocumentResolver resolver)
        : IRequestHandler<ListInspectionArchiveQuery, ResponseDto<ArchiveResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ArchiveResult>> Handle(
            ListInspectionArchiveQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var page = await store.ListAsync(
                new ArchiveFilter(
                    query.DivisionId, query.CategoryId, query.Severity, query.RemediationStatus,
                    query.DetectedFrom, query.DetectedTo, query.RefSearch, query.Page, query.PageSize),
                cancellationToken);

            // Разрешение ссылок — только для страницы (партия мала по построению страницы).
            var refs = page.Groups
                .Where(g => g.ReferenceDocRef is not null)
                .Select(g => g.ReferenceDocRef!)
                .Distinct(StringComparer.Ordinal)
                .ToList();
            var cards = await resolver.ResolveAsync(refs, cancellationToken);

            return ResponseDto<ArchiveResult>.Ok(new ArchiveResult(
                [.. page.Groups.Select(g => new ArchiveGroupView(
                    g,
                    g.ReferenceDocRef is { } reference ? cards.GetValueOrDefault(reference) : null))],
                page.TotalGroups));
        }
    }
}
