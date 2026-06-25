using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Search;

using ResModel = ResponseDto<SearchNpaResult>;

/// <summary>
/// Семантический поиск по корпусу НПА (Э4-07, ТФ-НПА-01/02) с учётом допуска (GATE-1) и актуальности
/// редакций (GATE-3). Обращение аудируется (ТБ-030).
/// </summary>
/// <param name="Query">Поисковый запрос инспектора (по смыслу).</param>
public sealed record SearchNpaQuery(string Query) : IRequest<ResModel>
{
    /// <summary>
    /// Обработчик: контекст доступа → ядровой <see cref="IRetriever"/> (фильтр доступа + только актуальные) →
    /// проекция в результат → аудит <see cref="AuditAction.Search"/>.
    /// </summary>
    public sealed class Handler(
        IRetriever retriever,
        IAccessContextProvider accessContextProvider,
        IAuditWriter auditWriter) : IRequestHandler<SearchNpaQuery, ResModel>
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
                .Select(chunk => new NpaHit(chunk.DocumentId, chunk.Text, chunk.Score, chunk.IsCurrent))
                .ToList();

            // Аудит обращения (ТБ-030): гриф = максимум грифов выданных фрагментов (что реально получено).
            var classification = chunks.Count == 0 ? (short)0 : chunks.Max(chunk => chunk.Classification);
            await auditWriter.WriteAsync(
                new AuditEntry(AuditAction.Search, classification, PayloadSensitive: $"Поиск НПА: {query.Query}"),
                cancellationToken);

            return ResModel.Ok(new SearchNpaResult(hits), hits.Count);
        }
    }
}
