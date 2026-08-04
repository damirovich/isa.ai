using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Abstractions.Conversations;

/// <summary>
/// Один ход диалога — реплика пользователя или ответ ассистента. Передаётся в модель как история для
/// многоходового (грунтованного) общения: контекст диалога сохраняется, но грунтовка КАЖДОГО нового
/// ответа по-прежнему выполняется против свежих фрагментов текущего запроса.
/// </summary>
/// <param name="Role">Автор реплики.</param>
/// <param name="Text">Текст реплики.</param>
public sealed record ChatTurn(ConversationMessageRole Role, string Text);
