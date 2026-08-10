using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Упоминание участника в комментарии (ТЗ СКИД §4.8). Сохраняются ТОЛЬКО прошедшие двойную проверку
/// (см. <c>ICommentStore</c>): токен реально есть в тексте И пользователь заявлен клиентом И существует
/// в реестре — ни тексту, ни списку от клиента по отдельности не доверяем (перенос решения СКИД).
/// </summary>
public class DocumentCommentMention : AuditableEntity
{
    /// <summary>Комментарий (FK внутри схемы).</summary>
    public int CommentId { get; set; }

    /// <summary>Упомянутый — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int UserId { get; set; }
}
