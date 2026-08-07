using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Audit;

/// <summary>Отбор записей журнала аудита для просмотра.</summary>
/// <param name="From">Начало периода (UTC, включительно); <see langword="null"/> — без нижней границы.</param>
/// <param name="To">Конец периода (UTC, включительно); <see langword="null"/> — без верхней границы.</param>
/// <param name="SubjectId">Субъект действия; <see langword="null"/> — любой.</param>
/// <param name="Action">Тип действия; <see langword="null"/> — любой.</param>
/// <param name="ObjectRef">Подстрока идентификатора объекта; <see langword="null"/> — любой.</param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы.</param>
public sealed record AuditFilter(
    DateTime? From = null,
    DateTime? To = null,
    int? SubjectId = null,
    AuditAction? Action = null,
    string? ObjectRef = null,
    int Page = 1,
    int PageSize = 50);

/// <summary>Строка журнала для показа.</summary>
public sealed record AuditRecordRow(
    long Id,
    DateTime OccurredAt,
    int? SubjectId,
    string? SubjectName,
    AuditAction Action,
    string? ObjectRef,
    short Classification,
    int? DivisionId,
    string? PayloadSensitive);

/// <summary>Страница журнала: строки и общее число подходящих записей (для постраничной навигации).</summary>
public sealed record AuditPage(IReadOnlyList<AuditRecordRow> Rows, int TotalCount);

/// <summary>
/// Чтение неизменяемого журнала аудита (ТБ-030/032).
/// </summary>
/// <remarks>
/// ОТДЕЛЬНЫЙ ПОРТ от <see cref="IAuditWriter"/> намеренно: писать в журнал обязаны все сценарии,
/// а читать — единицы, и объединение дало бы каждому обработчику заодно и доступ на чтение всей
/// истории системы.
///
/// РАЗГРАНИЧЕНИЕ (ТБ-032) применяется В ЗАПРОСЕ: запись несёт гриф и подразделение своего объекта,
/// и субъекту выдаются только записи с грифом не выше его допуска. Иначе журнал стал бы обходным
/// каналом: по строке «просмотр документа №17, гриф 3» видно и существование документа, и его гриф,
/// а <see cref="AuditRecordRow.PayloadSensitive"/> несёт ещё и содержательную часть события.
/// Право САМОГО просмотра журнала (роль) проверяет вызывающий сценарий — роли ведёт профиль.
/// </remarks>
public interface IAuditReader
{
    /// <summary>Страница журнала в пределах допуска субъекта, новые записи первыми.</summary>
    Task<AuditPage> QueryAsync(
        AuditFilter filter, AccessContext access, CancellationToken cancellationToken = default);
}
