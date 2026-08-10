using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

/// <summary>
/// Кандидаты на привязку к норме — обслуживает диалог ВЕДЕНИЯ картотеки, поэтому под тем же гардом,
/// что команды (первое ревью нашло здесь утечку: без гарда и решётки запрос отдавал заголовки
/// и грифы ВСЕХ документов корпуса любому вошедшему). Заголовки и грифы кандидатов — только
/// в пределах решётки допуска субъекта (ТБ-020/021, фильтр в запросе хранилища).
/// </summary>
public sealed record SearchCorpusDocumentsQuery(int NormId, string? Text)
    : IRequest<ResponseDto<IReadOnlyList<CorpusDocumentCandidate>>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm:{NormId}:corpus-candidates:text={Text}";

    /// <inheritdoc cref="SearchCorpusDocumentsQuery" />
    public sealed class Handler(
        INormRegistryStore store,
        IAccessContextProvider accessProvider,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider)
        : IRequestHandler<SearchCorpusDocumentsQuery, ResponseDto<IReadOnlyList<CorpusDocumentCandidate>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<CorpusDocumentCandidate>>> Handle(
            SearchCorpusDocumentsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<IReadOnlyList<CorpusDocumentCandidate>>.BadRequest(NormGuard.Denied);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var candidates = await store.SearchCorpusDocumentsAsync(
                query.NormId, query.Text, access, cancellationToken);
            return ResponseDto<IReadOnlyList<CorpusDocumentCandidate>>.Ok(candidates);
        }
    }
}
