using ISC.AI.Abstractions.Audit;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Запись неизменяемого журнала аудита (<c>core.audit_record</c>, ТБ-030/031/032). НЕ наследует
/// <c>BaseEntity</c>: у журнала собственный монотонный ключ <see cref="long"/> (bigint-последовательность),
/// и он append-only — не аудируется и не удаляется мягко.
/// </summary>
/// <remarks>
/// Неизменность и упорядочение обеспечиваются хеш-цепочкой (<see cref="PrevHash"/> → <see cref="RecordHash"/>)
/// и <see cref="OccurredAt"/>. Чувствительное содержимое (<see cref="PayloadSensitive"/>) отделено от
/// метаданных и доступно по решётке грифа/подразделения (ТБ-032).
/// </remarks>
public class AuditRecordEntity
{
    /// <summary>Монотонный идентификатор записи (bigint-последовательность).</summary>
    public long Id { get; set; }

    /// <summary>Время события (UTC). NOT NULL.</summary>
    public DateTime OccurredAt { get; set; }

    /// <summary>Субъект (пользователь), если известен.</summary>
    public int? SubjectId { get; set; }

    /// <summary>Тип действия.</summary>
    public AuditAction Action { get; set; }

    /// <summary>Идентификаторы затронутых объектов.</summary>
    public string? ObjectRef { get; set; }

    /// <summary>Гриф события/объекта (ТБ-032). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение события/объекта — для решётки доступа к журналу (ТБ-032).</summary>
    public int? DivisionId { get; set; }

    /// <summary>Чувствительное содержимое (под решёткой, ТБ-032). Отделено от метаданных.</summary>
    public string? PayloadSensitive { get; set; }

    /// <summary>Хеш предыдущей записи (звено цепочки, ТБ-031). Для первой записи — нули (генезис).</summary>
    public byte[] PrevHash { get; set; } = [];

    /// <summary>Хеш текущей записи (SHA-256 от <see cref="PrevHash"/> + каноничных полей записи).</summary>
    public byte[] RecordHash { get; set; } = [];
}
