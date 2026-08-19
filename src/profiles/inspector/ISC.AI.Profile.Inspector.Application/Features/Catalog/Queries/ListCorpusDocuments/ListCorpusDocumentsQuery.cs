using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Catalog;

/// <summary>
/// Страница каталога корпуса НПА (ТФ-НПА-01/02): документы корпуса со статусом действия, отбором
/// и сортировкой — вид «как в ЦБД Минюста». Решётка допуска — в запросе хранилища (ТБ-020/021).
/// </summary>
public sealed record ListCorpusDocumentsQuery(
    string? Text = null,
    string? DocType = null,
    CorpusDocumentCurrency? Currency = null,
    CorpusCatalogSort Sort = CorpusCatalogSort.DateDesc,
    int Page = 1,
    int PageSize = 25) : IRequest<ResponseDto<CorpusCatalogPage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:corpus:list:text={Text};type={DocType};currency={Currency};sort={Sort};page={Page}";

    /// <inheritdoc cref="ListCorpusDocumentsQuery" />
    public sealed class Handler(ICorpusCatalog catalog, IAccessContextProvider accessProvider)
        : IRequestHandler<ListCorpusDocumentsQuery, ResponseDto<CorpusCatalogPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CorpusCatalogPage>> Handle(
            ListCorpusDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var page = await catalog.ListAsync(
                new CorpusCatalogFilter(query.Text, query.DocType, query.Currency, query.Sort, query.Page, query.PageSize),
                access, cancellationToken);
            return ResponseDto<CorpusCatalogPage>.Ok(page);
        }
    }
}
