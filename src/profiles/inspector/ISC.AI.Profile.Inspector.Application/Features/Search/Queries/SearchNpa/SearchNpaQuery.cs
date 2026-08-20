using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Search;

using ResModel = ResponseDto<SearchNpaResult>;

/// <summary>
/// Семантический поиск по корпусу НПА (Э4-07, ТФ-НПА-01/02) с учётом допуска (GATE-1) и актуальности
/// редакций (GATE-3). Обращение аудируется сквозным AuditBehavior (ТБ-030, Э4-11).
/// </summary>
/// <param name="Query">Поисковый запрос инспектора (по смыслу).</param>
public sealed record SearchNpaQuery(string Query) : IRequest<ResModel>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => $"Поиск НПА: {Query}";

    /// <summary>
    /// Обработчик: контекст доступа → ядровой <see cref="IRetriever"/> (фильтр доступа + только актуальные) →
    /// проекция в результат. Аудит обращения (<see cref="AuditAction.Search"/>) пишет сквозное AuditBehavior (Э4-11).
    /// </summary>
    public sealed class Handler(
        IRetriever retriever,
        IAccessContextProvider accessContextProvider) : IRequestHandler<SearchNpaQuery, ResModel>
    {
        private const int TopK = 10;

        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(SearchNpaQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            // Извлечение с ОБЯЗАТЕЛЬНЫМ фильтром доступа (GATE-1) и только актуальных (GATE-3) — гарантирует ядро.
            var chunks = await retriever.RetrieveAsync(query.Query, access, TopK, cancellationToken: cancellationToken);

            var hits = chunks
                .Select(chunk => new NpaHit(
                    chunk.DocumentId, chunk.Text, chunk.Score, chunk.IsCurrent, DocFlowDocumentId(chunk)))
                .ToList();

            return ResModel.Ok(new SearchNpaResult(hits), hits.Count);
        }

        // Метаданные — «багаж» профиля, ядро их не интерпретирует: ключ docflow_document_id кладёт
        // индексатор документооборота, а смысл ему придают только здесь и в чате (этап 7.2 Э4-35).
        private static int? DocFlowDocumentId(RetrievedChunk chunk) =>
            chunk.Metadata?.GetValueOrDefault("docflow_document_id") is { Length: > 0 } raw
                && int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var id)
                ? id
                : null;
    }
}
