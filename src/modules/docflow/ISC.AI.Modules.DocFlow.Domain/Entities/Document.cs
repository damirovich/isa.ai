using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Документ — центральный объект документооборота (ТЗ СКИД §1.4, §3.2). Схема <c>docflow</c>.
/// Поведение определяет группа его типа: «Хранение» — регистрация и хранение; «Исполнение» —
/// назначения по подразделениям (<see cref="DocumentAssignment"/>), каждое со своим сроком и статусом.
/// </summary>
/// <remarks>
/// Отличия от исходника СКИД (Э4-35 этап 2): ключи int (конвенция ядра); ссылки на пользователей —
/// слабые int на <c>core.app_user</c> БЕЗ FK через границу схем (ТО-инф-06); добавлены ОБЯЗАТЕЛЬНЫЕ
/// режимные метаданные — гриф и подразделение (ADR-0017 п.5, ТБ-002/020): в СКИД их не было, у нас без
/// них документ не пройдёт фильтр доступа и не попадёт в индекс корпуса (этап 7).
/// </remarks>
public class Document : AuditableEntity, IClassified
{
    /// <summary>Регистрационный номер (уникален, вводится вручную; может отсутствовать до присвоения).</summary>
    public string? RegNumber { get; set; }

    /// <summary>Дата регистрации (ТЗ §3.2 — обязательное поле).</summary>
    public DateOnly RegDate { get; set; }

    /// <summary>Тип документа (справочник <see cref="DocumentType"/>; определяет группу поведения).</summary>
    public int TypeId { get; set; }

    /// <summary>Навигация к типу.</summary>
    public DocumentType? Type { get; set; }

    /// <summary>Признак направленности (входящий/внутренний/исходящий) — на процесс не влияет.</summary>
    public DocumentDirection DirectionFlag { get; set; }

    /// <summary>Источник (информативность и отчётность; на процесс не влияет).</summary>
    public string? Source { get; set; }

    /// <summary>Краткое содержание (обязательное поле, §3.2).</summary>
    public required string ShortContent { get; set; }

    /// <summary>Полный текст (опционально; используется индексацией в корпус, этап 7).</summary>
    public string? FullText { get; set; }

    /// <summary>Приоритет — только для группы «Исполнение» (§3.2), иначе <see langword="null"/>.</summary>
    public DocumentPriority? Priority { get; set; }

    /// <summary>
    /// Ответственный инспектор (только «Исполнение», один на весь документ, §3.2).
    /// Слабая ссылка на <c>core.app_user</c> по значению — без FK через границу схем (ТО-инф-06).
    /// </summary>
    public int? InspectorUserId { get; set; }

    /// <summary>
    /// Агрегированный статус — вычисляется по статусам всех назначений
    /// (<see cref="Services.AggregatedStatusCalculator"/>, §4.3), вручную не выставляется.
    /// </summary>
    public DocumentAggregatedStatus AggregatedStatus { get; set; } = DocumentAggregatedStatus.NotApplicable;

    /// <summary>Примечания (опционально).</summary>
    public string? Notes { get; set; }

    /// <summary>Кто зарегистрировал (слабая ссылка на <c>core.app_user</c>; аудит действий — в журнале ядра).</summary>
    public int? RegisteredByUserId { get; set; }

    /// <summary>Гриф (уровень: выше — строже). NOT NULL — основа фильтра доступа (ТБ-020, ADR-0017 п.5).</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение-владелец. NOT NULL. Тот же словарь, что решётка доступа ядра; слабая ссылка (ТО-инф-06).</summary>
    public int DivisionId { get; set; }

    /// <summary>Токен оптимистической блокировки — системная колонка PostgreSQL <c>xmin</c> (как в СКИД).</summary>
    public uint Xmin { get; set; }

    /// <summary>Назначения по подразделениям (только группа «Исполнение», §4.1).</summary>
    public ICollection<DocumentAssignment> Assignments { get; set; } = [];

    /// <summary>Версионируемые файлы документа (§3.3).</summary>
    public ICollection<DocumentFile> Files { get; set; } = [];
}
