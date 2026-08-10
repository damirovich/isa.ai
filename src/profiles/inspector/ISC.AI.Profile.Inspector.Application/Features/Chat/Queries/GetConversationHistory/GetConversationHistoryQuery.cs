using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>История сообщений диалога (для владельца).</summary>
public sealed record GetConversationHistoryQuery(int ConversationId) : IRequest<ResponseDto<IReadOnlyList<ChatTurn>>>
{
    /// <inheritdoc cref="GetConversationHistoryQuery" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<GetConversationHistoryQuery, ResponseDto<IReadOnlyList<ChatTurn>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ChatTurn>>> Handle(
            GetConversationHistoryQuery query, CancellationToken cancellationToken)
        {
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<IReadOnlyList<ChatTurn>>.Ok([]);
            }

            var history = await store.GetHistoryAsync(query.ConversationId, subjectId, cancellationToken);
            return ResponseDto<IReadOnlyList<ChatTurn>>.Ok(history);
        }
    }
}

