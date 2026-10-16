using System.Globalization;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Chat.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Chat.Application.Features;

/// <summary>Переименовать свой диалог (ADR-0052). Диалог недоступной области — «не найден» (ТБ-021).</summary>
/// <param name="ConversationId">Диалог.</param>
/// <param name="Title">Новое название.</param>
public sealed record RenameChatConversationCommand(int ConversationId, string Title) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"chat:conversation:{ConversationId.ToString(CultureInfo.InvariantCulture)}:rename";

    /// <inheritdoc cref="RenameChatConversationCommand" />
    public sealed class Handler(IChatProfile profile, IConversationStore conversations, IAccessContextProvider accessProvider)
        : IRequestHandler<RenameChatConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(RenameChatConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            return await OwnConversation.FindAsync(profile, conversations, accessProvider, command.ConversationId, cancellationToken) is { } me
                && await conversations.RenameAsync(command.ConversationId, me, ChatGate.Title(command.Title), cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(ChatMessages.NotFound);
        }
    }
}

/// <summary>
/// Удалить свой диалог (ADR-0052): диалог скрывается из списка без восстановления (мягкое удаление ядра). Записи
/// журнала о вопросах остаются (ТБ-030); физически переписку уничтожает профиль вместе со своим объектом (ТБ-064).
/// </summary>
/// <param name="ConversationId">Диалог.</param>
public sealed record DeleteChatConversationCommand(int ConversationId) : IRequest<ResponseDto<bool>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"chat:conversation:{ConversationId.ToString(CultureInfo.InvariantCulture)}:delete";

    /// <inheritdoc cref="DeleteChatConversationCommand" />
    public sealed class Handler(IChatProfile profile, IConversationStore conversations, IAccessContextProvider accessProvider)
        : IRequestHandler<DeleteChatConversationCommand, ResponseDto<bool>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<bool>> Handle(DeleteChatConversationCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);
            return await OwnConversation.FindAsync(profile, conversations, accessProvider, command.ConversationId, cancellationToken) is { } me
                && await conversations.DeleteAsync(command.ConversationId, me, cancellationToken)
                ? ResponseDto<bool>.Ok(true)
                : ResponseDto<bool>.NotFound(ChatMessages.NotFound);
        }
    }
}

/// <summary>Свой диалог видимой области: владелец, а область — по-прежнему видна (ТБ-021).</summary>
internal static class OwnConversation
{
    /// <summary>Числовой идентификатор владельца, если диалог его и область видна; иначе <see langword="null"/>.</summary>
    /// <remarks>
    /// Права профиля на сам чат здесь НЕ требуется (как и раньше у обоих профилей): сотрудник, у которого чат
    /// отобрали, может переименовать или убрать СВОЙ диалог — это не открывает ему ни материалов, ни модели.
    /// </remarks>
    public static async Task<int?> FindAsync(
        IChatProfile profile, IConversationStore conversations, IAccessContextProvider accessProvider, int conversationId,
        CancellationToken cancellationToken)
    {
        var access = await accessProvider.GetCurrentAsync(cancellationToken);
        if (access.NumericSubjectId is not { } me
            || await conversations.FindAsync(conversationId, me, cancellationToken) is not { } summary
            || await profile.ResolveAreaAsync(summary.Area, access, cancellationToken) is null)
        {
            return null;
        }

        return me;
    }
}
