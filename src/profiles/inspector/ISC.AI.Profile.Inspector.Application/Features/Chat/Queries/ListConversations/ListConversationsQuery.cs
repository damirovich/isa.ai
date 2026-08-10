using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>Список диалогов текущего пользователя (боковая панель).</summary>
public sealed record ListConversationsQuery : IRequest<ResponseDto<IReadOnlyList<ConversationSummary>>>
{
    /// <inheritdoc cref="ListConversationsQuery" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<ListConversationsQuery, ResponseDto<IReadOnlyList<ConversationSummary>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ConversationSummary>>> Handle(
            ListConversationsQuery query, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<IReadOnlyList<ConversationSummary>>.Ok([]);
            }

            var list = await store.ListAsync(subjectId, cancellationToken);
            return ResponseDto<IReadOnlyList<ConversationSummary>>.Ok(list);
        }
    }
}

