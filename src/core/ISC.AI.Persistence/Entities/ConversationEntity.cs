using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Диалог чата — доменно-нейтральный носитель истории общения пользователя с ассистентом. Схема <c>core</c>.
/// Принадлежит субъекту (<see cref="SubjectId"/>) — по нему разграничивается доступ (диалог содержит ДСП).
/// Мягко удаляемый (кнопка «удалить чат» скрывает из списка; неизменяемый журнал ДЕЙСТВИЙ ведётся отдельно).
/// </summary>
public class ConversationEntity : AuditableEntity, ISoftDeletable
{
    /// <summary>Заголовок диалога (обычно — начало первого запроса пользователя).</summary>
    public required string Title { get; set; }

    /// <summary>Владелец диалога (числовой идентификатор субъекта из контекста доступа). Основа разграничения.</summary>
    public int SubjectId { get; set; }

    /// <summary>Максимальный гриф содержимого диалога — для маркировки и потенциальной фильтрации.</summary>
    public short Classification { get; set; }

    /// <summary>
    /// Область профиля, в которой ведётся диалог (ADR-0052): строка, смысл которой знает только профиль (например,
    /// <c>case:45</c>). <see langword="null"/> — область по умолчанию, в том числе у диалогов, заведённых до областей.
    /// </summary>
    public string? Area { get; set; }

    /// <summary>
    /// Ключи уничтожения (ADR-0052, ТБ-064): область и материалы, на которые опирались ответы. Профиль уничтожает свой
    /// объект — и уничтожает все диалоги с его ключом. Ядро ключи не толкует.
    /// </summary>
    public List<string> ScopeKeys { get; set; } = [];

    /// <inheritdoc />
    public bool IsDeleted { get; set; }

    /// <summary>Сообщения диалога (каскадно удаляются вместе с диалогом).</summary>
    public ICollection<ConversationMessageEntity> Messages { get; set; } = [];
}
