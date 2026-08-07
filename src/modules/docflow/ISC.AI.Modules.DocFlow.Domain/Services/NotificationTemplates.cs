using System.Globalization;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>
/// Шаблоны текстов уведомлений (разд. 5 ТЗ СКИД). Ключ + аргументы хранятся в БД, текст собирается
/// здесь при чтении — чтобы двуязычие (ТЗ п. 7.1) можно было добавить, не переписывая накопленные
/// строки. Один вид уведомления может иметь НЕСКОЛЬКО шаблонов (перенос решения СКИД: «вам назначено»
/// и «назначено такому-то» — один вид <c>Assigned</c>, но разные тексты для исполнителя и наблюдателей).
/// </summary>
public static class NotificationTemplates
{
    /// <summary>Срок приближается (за горизонт уведомлений).</summary>
    public const string DeadlineApproaching = "deadline.approaching";

    /// <summary>Срок сегодня.</summary>
    public const string DeadlineToday = "deadline.today";

    /// <summary>Переведено в «Просрочено» системой.</summary>
    public const string Overdue = "deadline.overdue";

    /// <summary>Назначение выдано лично получателю.</summary>
    public const string AssignedToYou = "assignment.assigned-to-you";

    /// <summary>Назначение выдано другому (уведомление наблюдателю — инспектору документа).</summary>
    public const string AssignedNotice = "assignment.assigned-notice";

    /// <summary>Статус назначения изменён.</summary>
    public const string StatusChanged = "assignment.status-changed";

    /// <summary>Срок назначения продлён.</summary>
    public const string DeadlineExtended = "assignment.deadline-extended";

    /// <summary>Назначение переведено на получателя (§4.7).</summary>
    public const string ReassignedToYou = "assignment.reassigned-to-you";

    /// <summary>Назначение снято с получателя и передано другому (§4.7).</summary>
    public const string ReassignedFromYou = "assignment.reassigned-from-you";

    /// <summary>Исполнитель сменился (уведомление наблюдателю — инспектору документа).</summary>
    public const string ReassignedNotice = "assignment.reassigned-notice";

    /// <summary>Вы назначены ответственным инспектором документа (правка карточки, §3.2).</summary>
    public const string InspectorAssignedToYou = "document.inspector-assigned-to-you";

    /// <summary>Вы больше не ответственный инспектор документа (§3.2).</summary>
    public const string InspectorRemovedFromYou = "document.inspector-removed-from-you";

    /// <summary>Упоминание в комментарии.</summary>
    public const string MentionedInComment = "comment.mentioned";

    /// <summary>Добавлен комментарий к документу.</summary>
    public const string CommentAdded = "comment.added";

    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        [DeadlineApproaching] = "До срока по документу {document} осталось дней: {days}. Срок: {deadline}.",
        [DeadlineToday] = "Сегодня срок по документу {document}.",
        [Overdue] = "Документ {document}: срок истёк, назначение переведено в «Просрочено».",
        [AssignedToYou] = "Вам назначен документ {document}. Срок: {deadline}.",
        [AssignedNotice] = "По документу {document} создано назначение. Срок: {deadline}.",
        [StatusChanged] = "Документ {document}: статус назначения — «{status}» (изменил: {actor}).",
        [DeadlineExtended] = "Документ {document}: срок продлён с {old} на {new}.",
        [ReassignedToYou] = "Вы назначены исполнителем по документу {document}. Срок: {deadline}.",
        [ReassignedFromYou] = "Документ {document}: исполнение передано другому исполнителю ({actor}).",
        [ReassignedNotice] = "Документ {document}: сменился исполнитель (изменил: {actor}).",
        [InspectorAssignedToYou] = "Вы назначены ответственным инспектором по документу {document} (изменил: {actor}).",
        [InspectorRemovedFromYou] = "Документ {document}: вы больше не ответственный инспектор (изменил: {actor}).",
        [MentionedInComment] = "{author} упомянул вас в комментарии к документу {document}.",
        [CommentAdded] = "{author} добавил комментарий к документу {document}.",
    };

    /// <summary>
    /// Человекочитаемое обозначение документа в тексте уведомления: регистрационный номер, а при его
    /// отсутствии — краткое содержание. Одно правило на все уведомления (в проекциях EF оно повторено
    /// выражением: вызов метода в SQL не транслируется).
    /// </summary>
    public static string DocumentTitle(string? regNumber, string shortContent) =>
        string.IsNullOrWhiteSpace(regNumber) ? $"б/н «{shortContent}»" : regNumber;

    /// <summary>
    /// Собирает текст уведомления. Неизвестный ключ (шаблон удалён/переименован, а строки в БД
    /// остались) отдаётся как есть — уведомление не теряется и не роняет ленту.
    /// </summary>
    public static string Render(string messageKey, IReadOnlyDictionary<string, string>? arguments)
    {
        if (!Russian.TryGetValue(messageKey, out var template))
        {
            return messageKey;
        }

        if (arguments is null || arguments.Count == 0)
        {
            return template;
        }

        foreach (var (name, value) in arguments)
        {
            template = template.Replace(
                string.Create(CultureInfo.InvariantCulture, $"{{{name}}}"), value, StringComparison.Ordinal);
        }

        return template;
    }
}
