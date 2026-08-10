using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Уведомление пользователя (разд. 5 ТЗ СКИД) — одна строка НА ПОЛУЧАТЕЛЯ (перенос решения СКИД:
/// одно событие с несколькими адресатами даёт несколько строк).
/// </summary>
/// <remarks>
/// Хранится НЕ готовый текст, а ключ шаблона + аргументы (перенос контракта СКИД): двуязычие рус/кырг
/// — требование ТЗ п. 7.1, ещё не реализованное; готовый русский текст в строках сделал бы уже
/// накопленные уведомления непереводимыми задним числом. Рендер — при чтении (<c>NotificationTemplates</c>).
/// </remarks>
public class Notification : AuditableEntity
{
    /// <summary>Получатель — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int RecipientUserId { get; set; }

    /// <summary>Вид уведомления.</summary>
    public NotificationType Type { get; set; }

    /// <summary>Ключ шаблона текста (см. <c>NotificationTemplates</c>).</summary>
    public required string MessageKey { get; set; }

    /// <summary>Аргументы шаблона (JSON-словарь «имя → значение»).</summary>
    public required string ArgumentsJson { get; set; }

    /// <summary>Документ, к которому относится уведомление (FK внутри схемы).</summary>
    public int? DocumentId { get; set; }

    /// <summary>Назначение (FK внутри схемы); <see langword="null"/> — уведомление документо-уровневое.</summary>
    public int? AssignmentId { get; set; }

    /// <summary>Комментарий (FK внутри схемы) — для перехода к обсуждению.</summary>
    public int? CommentId { get; set; }

    /// <summary>
    /// Срок, о котором уведомляли. Опора ДЕДУПЛИКАЦИИ уведомлений о сроках: повторно про ТОТ ЖЕ срок
    /// не напоминаем, а после продления (срок стал другим) напомним снова. <see langword="null"/> —
    /// уведомление не про срок.
    /// </summary>
    public DateOnly? AboutDeadline { get; set; }

    /// <summary>Прочитано получателем.</summary>
    public bool IsRead { get; set; }
}
