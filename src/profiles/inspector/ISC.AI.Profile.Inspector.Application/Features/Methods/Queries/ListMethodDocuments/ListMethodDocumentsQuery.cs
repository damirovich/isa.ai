using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Methods;

/// <summary>
/// Страница реестра методик с отбором (§5.2.9). РЕЖИМ: выдаются только методики с грифом
/// не выше допуска субъекта (ТБ-020-стиль) — фильтр в запросе хранилища.
/// </summary>
public sealed record ListMethodDocumentsQuery(
    string? ArtifactKind = null,
    MethodDocumentStatus? Status = null,
    string? Search = null,
    int Page = 1,
    int PageSize = 20) : IRequest<ResponseDto<MethodPage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:methods:list:kind={ArtifactKind};status={Status};search={Search};page={Page}";

    /// <inheritdoc cref="ListMethodDocumentsQuery" />
    public sealed class Handler(IMethodRegistryStore store, IAccessContextProvider accessContextProvider)
        : IRequestHandler<ListMethodDocumentsQuery, ResponseDto<MethodPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MethodPage>> Handle(
            ListMethodDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed: без контекста доступа провайдер бросает исключение, выборка не начнётся.
            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var page = await store.ListAsync(
                new MethodListFilter(query.ArtifactKind, query.Status, query.Search, query.Page, query.PageSize),
                access.MaxClassification,
                cancellationToken);
            return ResponseDto<MethodPage>.Ok(page);
        }
    }
}
