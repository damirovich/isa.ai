using System.Runtime.CompilerServices;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Chat;

/// <summary>Переименовать диалог владельца.</summary>
public sealed record RenameConversationCommand(int ConversationId, string Title) : IRequest<ResponseDto<bool>>
{
    /// <inheritdoc cref="RenameConversationCommand" />
    public sealed class Handler(IConversationStore store, IAccessContextProvider accessProvider)
        : IRequestHandler<RenameConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            if (access.NumericSubjectId is not { } subjectId)
            {
                return ResponseDto<bool>.Ok(false);
            }

            var ok = await store.RenameAsync(command.ConversationId, subjectId, command.Title, cancellationToken);
            return ResponseDto<bool>.Ok(ok);
        }
    }
}

