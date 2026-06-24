namespace ISC.AI.Abstractions.Audit;

/// <summary>
/// Писатель неизменяемого журнала аудита (ТБ-030/031). Единственный путь добавления записей:
/// только добавление (append-only), без обновления/удаления.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ БЕЗОПАСНОСТИ (ТБ-031): записи журнала неизменяемы. Реализация связывает их в
/// ХЕШ-ЦЕПОЧКУ (<c>prev_hash → record_hash</c>) для обнаружения подмены и НЕ выполняет UPDATE/DELETE;
/// запрет модификации дополнительно закрепляется на уровне БД (роль без UPDATE/DELETE и триггер).
/// Метаданные события и чувствительное содержимое разделены (ТБ-032, см. <see cref="AuditEntry"/>).
/// </remarks>
public interface IAuditWriter
{
    /// <summary>Добавляет запись в журнал, проставляя время и звено хеш-цепочки.</summary>
    Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default);
}
