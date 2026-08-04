using ISC.AI.Abstractions.Conversations;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Rag;

/// <summary>
/// СВОБОДНЫЙ (негрунтованный) режим чата: модель отвечает как обычный ассистент с учётом истории диалога,
/// БЕЗ извлечения НПА и БЕЗ грунтовки. Системное правило запрещает конкретные юридические утверждения и
/// ссылки на НПА «по памяти» (для них — грунтованный режим). Генерация аудируется (ТБ-030), гриф — 0
/// (обращения к ДСП нет). Отдаёт текст по кускам (стриминг).
/// </summary>
public interface IConversationalGenerator
{
    /// <summary>Потоковый свободный ответ: куски текста по мере генерации.</summary>
    IAsyncEnumerable<string> GenerateStreamingAsync(
        string query,
        IReadOnlyList<ChatTurn> history,
        AccessContext access,
        ModelRole role,
        CancellationToken cancellationToken = default);
}
