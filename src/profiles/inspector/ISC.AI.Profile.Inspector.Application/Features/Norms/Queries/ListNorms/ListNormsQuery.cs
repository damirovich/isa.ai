using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Страница реестра норм с отбором (ТФ-НПА-02). Карточки норм (номер, название, статусы редакций)
/// собственного грифа не несут — грифован ТЕКСТ в корпусе, а его этот запрос не отдаёт.
/// </summary>
public sealed record ListNormsQuery(
    string? Text = null,
    RevisionStatus? Status = null,
    int Page = 1,
    int PageSize = 25) : IRequest<ResponseDto<NormRegistryPage>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norms:list:text={Text};status={Status};page={Page}";

    /// <inheritdoc cref="ListNormsQuery" />
    public sealed class Handler(INormRegistryStore store) : IRequestHandler<ListNormsQuery, ResponseDto<NormRegistryPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<NormRegistryPage>> Handle(
            ListNormsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var page = await store.ListAsync(
                new NormListFilter(query.Text, query.Status, query.Page, query.PageSize), cancellationToken);
            return ResponseDto<NormRegistryPage>.Ok(page);
        }
    }
}
