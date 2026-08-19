using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Catalog;

/// <summary>
/// Карточка документа корпуса с текстом по фрагментам (ТФ-НПА-02: утратившие силу фрагменты
/// помечаются). Просмотр текста аудируется (<see cref="AuditAction.View"/>, ТБ-030); гриф записи —
/// не ниже грифа документа, поэтому после чтения хендлер сообщает его через результат, а до чтения
/// известен только субъект. Недоступный документ неотличим от несуществующего.
/// </summary>
public sealed record GetCorpusDocumentQuery(int DocumentId)
    : IRequest<ResponseDto<CorpusDocumentDetails>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:corpus:document:{DocumentId}:view";

    /// <inheritdoc cref="GetCorpusDocumentQuery" />
    public sealed class Handler(ICorpusCatalog catalog, IAccessContextProvider accessProvider)
        : IRequestHandler<GetCorpusDocumentQuery, ResponseDto<CorpusDocumentDetails>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CorpusDocumentDetails>> Handle(
            GetCorpusDocumentQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var details = await catalog.GetAsync(query.DocumentId, access, cancellationToken);
            return details is null
                ? ResponseDto<CorpusDocumentDetails>.NotFound("Документ не найден.")
                : ResponseDto<CorpusDocumentDetails>.Ok(details);
        }
    }
}
