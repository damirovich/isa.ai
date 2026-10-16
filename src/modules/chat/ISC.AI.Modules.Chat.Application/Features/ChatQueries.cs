using System.Globalization;
using ISC.AI.Abstractions.AI;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>
/// Чат области (ADR-0052): область, свои диалоги в ней, строка «что видит чат» и подсказки. Свой список — без записи в
/// журнал; материалы не готовятся (только строка готовности по уже положенному).
/// </summary>
/// <param name="AreaKey">Область; <see langword="null"/> — область профиля по умолчанию.</param>
public sealed record GetChatOverviewQuery(string? AreaKey) : IRequest<ResponseDto<ChatOverview>>
{
    /// <inheritdoc cref="GetChatOverviewQuery" />
    public sealed class Handler(IChatProfile profile, IConversationStore conversations, IAccessContextProvider accessProvider)
        : IRequestHandler<GetChatOverviewQuery, ResponseDto<ChatOverview>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ChatOverview>> Handle(GetChatOverviewQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            var entry = await ChatGate.EnterAsync(profile, accessProvider, query.AreaKey, cancellationToken);
            if (entry is not { Area: { } area, Access: { } access })
            {
                return entry.Error == ChatMessages.NoRight
                    ? ResponseDto<ChatOverview>.BadRequest(ChatMessages.NoRight)
                    : ResponseDto<ChatOverview>.NotFound(entry.Error ?? ChatMessages.NotFound);
            }

            // Без числового сотрудника диалогов нет и быть не может — пустой список, а не отказ: чат виден, но
            // вопрос ответит понятным отказом.
            var list = entry.SubjectId is { } me
                ? await conversations.ListInAreaAsync(me, area.Key, cancellationToken)
                : [];

            string? coverage = null;
            string? hint = null;
            try
            {
                var scope = await profile.ResolveScopeAsync(area, access, forQuestion: false, cancellationToken);
                (coverage, hint) = (scope.Coverage, scope.CoverageHint);
            }
            catch (ModelUnavailableException)
            {
                // Строка готовности к модели не обращается; если профиль всё же обратился — чат открывается без неё.
            }

            return ResponseDto<ChatOverview>.Ok(new ChatOverview(area, list, coverage, hint, profile.Suggestions(area)));
        }
    }
}

/// <summary>Области, в которых сотрудник может спрашивать, — для выбора на странице чата (ADR-0052).</summary>
public sealed record ListChatAreasQuery : IRequest<ResponseDto<IReadOnlyList<ChatArea>>>
{
    /// <inheritdoc cref="ListChatAreasQuery" />
    public sealed class Handler(IChatProfile profile, IAccessContextProvider accessProvider)
        : IRequestHandler<ListChatAreasQuery, ResponseDto<IReadOnlyList<ChatArea>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ChatArea>>> Handle(ListChatAreasQuery query, CancellationToken cancellationToken)
        {
            if (!await profile.CanUseAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<ChatArea>>.BadRequest(ChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var areas = await profile.ListAreasAsync(access, cancellationToken);
            return ResponseDto<IReadOnlyList<ChatArea>>.Ok(areas, areas.Count);
        }
    }
}

/// <summary>
/// Свой диалог с сообщениями и источниками (ADR-0052). Чтение переписки — запись <see cref="AuditAction.View"/>.
/// Диалог виден, пока видна его область (ТБ-021); сообщения выше текущего допуска скрыты (ТБ-020).
/// </summary>
/// <param name="ConversationId">Диалог.</param>
public sealed record GetChatConversationQuery(int ConversationId) : IRequest<ResponseDto<ChatThreadView>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"chat:conversation:{ConversationId.ToString(CultureInfo.InvariantCulture)}:view";

    /// <inheritdoc cref="GetChatConversationQuery" />
    public sealed class Handler(IChatProfile profile, IConversationStore conversations, IAccessContextProvider accessProvider)
        : IRequestHandler<GetChatConversationQuery, ResponseDto<ChatThreadView>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<ChatThreadView>> Handle(GetChatConversationQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);
            if (!await profile.CanUseAsync(cancellationToken))
            {
                return ResponseDto<ChatThreadView>.BadRequest(ChatMessages.NoRight);
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } me
                || await conversations.GetAsync(query.ConversationId, me, access.MaxClassification, cancellationToken) is not { } thread
                || await profile.ResolveAreaAsync(thread.Area, access, cancellationToken) is null)
            {
                return ResponseDto<ChatThreadView>.NotFound(ChatMessages.NotFound);
            }

            return ResponseDto<ChatThreadView>.Ok(new ChatThreadView(
                thread.Id,
                thread.Area,
                thread.Title,
                thread.Classification,
                thread.Messages
                    .Select(m => new ChatMessageView(
                        m.Id, m.Role, m.Content, m.Classification, m.CreatedAt, ChatJson.ReadSources(m.SourcesJson), ChatJson.ReadCheck(m.CheckJson)))
                    .ToList(),
                thread.HiddenMessages));
        }
    }
}
