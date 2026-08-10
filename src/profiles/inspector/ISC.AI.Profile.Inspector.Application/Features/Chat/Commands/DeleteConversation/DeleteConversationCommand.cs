using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>Удалить (мягко) диалог владельца.</summary>
public sealed record DeleteConversationCommand(int ConversationId) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="DeleteConversationCommand" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<bool>.Ok(false);
            }

            var ok = await store.DeleteAsync(command.ConversationId, subjectId, cancellationToken);
            return ResponseDto<bool>.Ok(ok);
        }
    }
}
