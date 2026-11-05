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
/// <param name="Outcome">Исход операции (ТБ-080): по умолчанию — успех; отказы и сбои сценарий указывает явно.</param>
/// <param name="ClientAddress">
/// IP-адрес клиента (ТБ-080). <see langword="null"/> — писатель берёт адрес из контекста субъекта сам
/// (<c>ISubjectProvider</c>, ADR-0066); явно передаётся, только если сценарий знает его точнее.
/// </param>
/// <param name="SessionId">
/// Сеанс входа субъекта (ТБ-080). <see langword="null"/> — писатель берёт его из контекста субъекта сам; явно —
/// например, запись успешного входа несёт номер сеанса, который будет открыт.
/// </param>
public sealed record AuditEntry(
    AuditAction Action,
    short Classification,
    int? SubjectId = null,
    string? ObjectRef = null,
    int? DivisionId = null,
    string? PayloadSensitive = null,
    AuditOutcome Outcome = AuditOutcome.Success,
    string? ClientAddress = null,
    Guid? SessionId = null);
