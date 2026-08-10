using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Уведомление в ленте получателя (текст уже собран из шаблона).</summary>
public sealed record NotificationItem(
    int Id,
    NotificationType Type,
    string Message,
    int? DocumentId,
    int? CommentId,
    bool IsRead,
    DateTime CreatedAt);

/// <summary>
/// Одно уведомление к отправке. <paramref name="AboutDeadline"/> заполняется ТОЛЬКО для уведомлений о
/// сроках — по нему работает дедупликация (см. <see cref="INotificationStore.RaiseAsync"/>).
/// </summary>
public sealed record NotificationDraft(
    IReadOnlyList<int> RecipientUserIds,
    NotificationType Type,
    string MessageKey,
    IReadOnlyDictionary<string, string> Arguments,
    int? DocumentId = null,
    int? AssignmentId = null,
    int? CommentId = null,
    DateOnly? AboutDeadline = null);

/// <summary>
/// Уведомления пользователей (разд. 5 ТЗ СКИД): «за N дней», «в день срока», просрочка, события по
/// назначениям и комментариям. Только в интерфейсе, без email (air-gap).
/// </summary>
public interface INotificationStore
{
    /// <summary>
    /// Создаёт уведомления (по строке на получателя) и возвращает число созданных.
    /// </summary>
    /// <remarks>
    /// ДЕДУПЛИКАЦИЯ уведомлений о СРОКАХ: если <c>AboutDeadline</c> задан, повторное уведомление того же
    /// вида про ТО ЖЕ назначение и ТОТ ЖЕ срок не создаётся. Так фоновая проверка, идущая раз в час,
    /// не превращается в ежечасный спам.
    ///
    /// Это ОСОЗНАННОЕ УЛУЧШЕНИЕ против СКИД, где дедуп был «скользящим окном 24 часа по паре
    /// (назначение, вид)». У окна два изъяна, оба подтверждены разбором исходника: (1) если в нужные
    /// сутки не отработал ни один тик (перезапуск, ночной простой), уведомление терялось НАВСЕГДА —
    /// СКИД искал строгое равенство «срок == сегодня + горизонт», назавтра условие уже ложно;
    /// (2) продление срока внутри тех же 24 часов не давало напомнить о НОВОМ сроке. Привязка к
    /// значению срока чинит оба: окно проверяется диапазоном, а другой срок — это другое уведомление.
    /// </remarks>
    Task<int> RaiseAsync(NotificationDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Непрочитанные уведомления получателя, новые первыми.</summary>
    /// <remarks>
    /// Лента фильтруется допуском НА ЭТАПЕ ВЫБОРКИ (инвариант 3, ТБ-020/021), а не только адресатом:
    /// текст уведомления содержит обозначение документа (рег. номер либо краткое содержание), то есть
    /// это выдача данных документа. Уведомление, чей документ субъекту сейчас недоступен (гриф понизили,
    /// подразделение сменили, роль отозвали), не показывается — иначе лента стала бы обходным каналом.
    /// Уведомления без документа (<c>DocumentId is null</c>) видит только сам адресат.
    /// </remarks>
    Task<IReadOnlyList<NotificationItem>> ListUnreadAsync(
        AccessContext access, int maxCount = 50, CancellationToken cancellationToken = default);

    /// <summary>
    /// Число непрочитанных (считается в БД, не по усечённому списку). Фильтруется тем же допуском,
    /// что и <see cref="ListUnreadAsync"/> — иначе счётчик выдавал бы наличие скрытых документов.
    /// </summary>
    Task<int> CountUnreadAsync(AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Помечает уведомление прочитанным; чужое — <see langword="false"/>, повтор идемпотентен.</summary>
    /// <remarks>
    /// Здесь достаточно адресата, без фильтра допуска: пометка «прочитано» ничего не выдаёт (текст не
    /// возвращается), а скрывать от человека возможность убрать из ленты уведомление, которое ему уже
    /// не показывают, смысла нет — иначе счётчик завис бы навсегда.
    /// </remarks>
    Task<bool> MarkReadAsync(int notificationId, int recipientUserId, CancellationToken cancellationToken = default);

    /// <summary>Помечает прочитанными все свои непрочитанные (в том числе скрытые фильтром допуска).</summary>
    Task MarkAllReadAsync(int recipientUserId, CancellationToken cancellationToken = default);
}
