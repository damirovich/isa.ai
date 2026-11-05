using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Audit;

/// <summary>Отбор записей журнала аудита для просмотра.</summary>
/// <param name="From">Начало периода (UTC, включительно); <see langword="null"/> — без нижней границы.</param>
/// <param name="To">Конец периода (UTC, включительно); <see langword="null"/> — без верхней границы.</param>
/// <param name="SubjectId">Субъект действия; <see langword="null"/> — любой.</param>
/// <param name="Action">Тип действия; <see langword="null"/> — любой.</param>
/// <param name="ObjectRef">
/// Подстрока, которая ищется в ссылке на объект, в сводке действия (<see cref="AuditRecordRow.PayloadSensitive"/>) —
/// сценарии пишут своё описание именно в сводку — и в IP-адресе клиента; без учёта регистра, символы % и _ буквальные.
/// <see langword="null"/> — любой.
/// </param>
/// <param name="Page">Номер страницы, с 1.</param>
/// <param name="PageSize">Размер страницы.</param>
/// <param name="Outcome">Исход операции (ТБ-080); <see langword="null"/> — любой, включая записи без исхода.</param>
/// <param name="SessionId">Сеанс входа — «все действия сеанса» (ТБ-080); <see langword="null"/> — любой.</param>
public sealed record AuditFilter(
    DateTime? From = null,
    DateTime? To = null,
    int? SubjectId = null,
    AuditAction? Action = null,
    string? ObjectRef = null,
    int Page = 1,
    int PageSize = 50,
    AuditOutcome? Outcome = null,
    Guid? SessionId = null);

/// <summary>Строка журнала для показа.</summary>
/// <param name="Outcome">Исход операции (ТБ-080); <see langword="null"/> — запись до ADR-0066, исход не записан.</param>
/// <param name="ClientAddress">IP-адрес клиента; <see langword="null"/> — не записан (фоновая работа, запись до ADR-0066).</param>
/// <param name="SessionId">Сеанс входа; <see langword="null"/> — не записан.</param>
public sealed record AuditRecordRow(
    long Id,
    DateTime OccurredAt,
    int? SubjectId,
    string? SubjectName,
    AuditAction Action,
    string? ObjectRef,
    short Classification,
    int? DivisionId,
    string? PayloadSensitive,
    AuditOutcome? Outcome = null,
    string? ClientAddress = null,
    Guid? SessionId = null);

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
