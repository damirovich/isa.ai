using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Retrieval;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>
/// Поиск по смыслу в материалах области (ТФ-ДДЛ-03, ADR-0051/0052): тем же ретривером ядра и в той же области, что
/// вопрос, но без модели — сотрудник видит сами фрагменты. Только если профиль включил режим.
/// </summary>
/// <remarks>
/// АУДИТ (ТБ-030): до поиска — запись <see cref="AuditAction.Search"/> на объект области с текстом запроса и наибольшим
/// грифом области; не записалось — поиска нет (fail-closed). Фильтр допуска к фрагментам применяет ядро (ТБ-020).
/// </remarks>
/// <param name="AreaKey">Область; <see langword="null"/> — область профиля по умолчанию.</param>
/// <param name="Text">Что искать (смысл, а не точные слова).</param>
public sealed record SearchChatMaterialsQuery(string? AreaKey, string Text) : IRequest<ResponseDto<ChatSenseResult>>
{
    /// <inheritdoc cref="SearchChatMaterialsQuery" />
    public sealed class Handler(
        IChatProfile profile,
        IRetriever retriever,
        IAuditWriter auditWriter,
        IAccessContextProvider accessProvider) : IRequestHandler<SearchChatMaterialsQuery, ResponseDto<ChatSenseResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ChatSenseResult>> Handle(SearchChatMaterialsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var text = ChatGate.Question(query.Text);
            if (text.Length == 0)
            {
                return ResponseDto<ChatSenseResult>.BadRequest(ChatMessages.EmptyQuestion);
            }

            if (!profile.Settings.AllowSenseSearch)
            {
                return ResponseDto<ChatSenseResult>.BadRequest(ChatMessages.SenseSearchOff);
            }

            var entry = await ChatGate.EnterAsync(profile, accessProvider, query.AreaKey, cancellationToken);
            if (entry is not { Area: { } area, Access: { } access })
            {
                return entry.Error == ChatMessages.NoRight
                    ? ResponseDto<ChatSenseResult>.BadRequest(ChatMessages.NoRight)
                    : ResponseDto<ChatSenseResult>.NotFound(entry.Error ?? ChatMessages.NotFound);
            }

            try
            {
                var scope = await profile.ResolveScopeAsync(area, access, forQuestion: true, cancellationToken);
                if (scope.Unavailable is not null || scope.DocumentIds is { Count: 0 })
                {
                    return ResponseDto<ChatSenseResult>.Ok(new ChatSenseResult([], 0));
                }

                await auditWriter.WriteAsync(
                    new AuditEntry(
                        AuditAction.Search,
                        area.AuditClassification,
                        SubjectId: access.NumericSubjectId,
                        ObjectRef: area.AuditRef + ":materials:sense-search",
                        DivisionId: area.DivisionId,
                        PayloadSensitive: $"Поиск по смыслу в материалах {area.Description}: {text}"),
                    cancellationToken);

                var fragments = await retriever.RetrieveAsync(
                    text,
                    access,
                    ChatGate.Clamp(profile.Settings.SenseSearchTopK, ChatLimits.MaxTopK),
                    scope.DocumentIds is null ? null : new RetrievalFilter(DocumentIds: scope.DocumentIds),
                    cancellationToken);
                var hits = fragments
                    .Select((f, index) => ChatSource.From(index + 1, f, scope) is { } source
                        ? new ChatSenseHit(source, f.Text, Math.Clamp(1 - f.Score, 0, 1))
                        : null)
                    .OfType<ChatSenseHit>()
                    .ToList();
                return ResponseDto<ChatSenseResult>.Ok(new ChatSenseResult(hits, scope.DocumentIds?.Count), hits.Count);
            }
            catch (ModelUnavailableException)
            {
                return ResponseDto<ChatSenseResult>.BadRequest(ChatMessages.SenseUnavailable);
            }
        }
    }
}
