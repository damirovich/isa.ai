namespace ISC.AI.Abstractions.Audit;

/// <summary>
/// Запись аудируемого события — вход для <see cref="IAuditWriter"/>. Несёт метаданные события и
/// (отдельно) чувствительное содержимое. Время события и хеш-цепочку проставляет писатель.
/// </summary>
/// <param name="Action">Тип действия.</param>
/// <param name="Classification">Гриф события/объекта (ТБ-032). Обязателен.</param>
/// <param name="SubjectId">Субъект (пользователь), если известен.</param>
/// <param name="ObjectRef">Идентификаторы затронутых объектов (например, documentId/chunkId).</param>
/// <param name="DivisionId">Подразделение события/объекта — для решётки доступа к самому журналу (ТБ-032).</param>
/// <param name="PayloadSensitive">
/// Чувствительное содержимое (тексты промптов/выводов, наименования ДСП). Хранится ОТДЕЛЬНО от
/// метаданных и под решёткой доступа (ТБ-032); срок хранения/обезличивание — ТБ-043/ТБ-064.
/// </param>
public sealed record AuditEntry(
    AuditAction Action,
    short Classification,
    int? SubjectId = null,
    string? ObjectRef = null,
    int? DivisionId = null,
    string? PayloadSensitive = null);
