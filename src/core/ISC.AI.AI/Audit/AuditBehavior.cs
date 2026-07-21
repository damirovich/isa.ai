using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using Mediator;
using Microsoft.Extensions.Logging;

namespace ISC.AI.AI.Audit;

/// <summary>
/// Сквозное поведение Mediator (ТБ-030, инвариант №4, §5.1.6.15): для КАЖДОГО аудируемого запроса
/// (<see cref="IAuditableRequest"/>) пишет запись в неизменяемый журнал (<see cref="IAuditWriter"/>).
/// Централизует аудит — его нельзя «забыть» в хендлере (именно из-за забывчивости генерация и загрузка
/// корпуса раньше проходили мимо журнала).
/// </summary>
/// <remarks>
/// FAIL-CLOSED (инвариант №4): если запись в журнал не удалась — исключение ПРОБРАСЫВАЕТСЯ (его оформит
/// внешний ExceptionHandlingBehavior в неуспешный ответ), результат пользователю не выдаётся: «нет записи —
/// нет выдачи». Это режимное решение (неотказуемость важнее доступности); при необходимости смягчается
/// владельцем режима. Обращение аудируется и при исключении хендлера, и при ОТМЕНЕ (фиксируем попытку —
/// данные могли быть уже извлечены/показаны). ГРИФ записи = <c>max(допуск субъекта, объявленный гриф
/// объекта)</c>: не ниже и допуска (данные ≤ допуска — GATE-1), и объявленного грифа загружаемого документа
/// (который для Ingest может быть ВЫШЕ допуска) — запись журнала никогда не НЕДО-классифицирована. Если
/// контекст доступа недоступен — гриф ставится МАКСИМАЛЬНЫМ (fail-closed по грифу), чтобы чувствительное
/// содержимое не оказалось под нижней решёткой. Ограничение по <see cref="IAuditableRequest"/> — поведение
/// применяется только к аудируемым сценариям.
/// </remarks>
public sealed class AuditBehavior<TMessage, TResponse>(
    IAuditWriter auditWriter,
    IAccessContextProvider accessContextProvider,
    ILogger<AuditBehavior<TMessage, TResponse>> logger)
    : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage, IAuditableRequest
    where TResponse : IResponseDto
{
    // При недоступном контексте доступа — наиболее ограничительный гриф (не 0!): запись остаётся под
    // максимальной решёткой, пока допуск неизвестен.
    private const short RestrictedClassificationOnUnknownAccess = short.MaxValue;

    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        TResponse response;
        try
        {
            response = await next(message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена: обращение могло уже коснуться данных — фиксируем попытку не-отменяемой записью.
            await WriteAuditAsync(message, CancellationToken.None);
            throw;
        }
        catch
        {
            // Обращение было, но упало — фиксируем факт и пробрасываем (ошибку оформит ExceptionHandlingBehavior).
            await WriteAuditAsync(message, cancellationToken);
            throw;
        }

        // Успешный путь — ВНЕ try выше: сбой записи журнала здесь fail-closed пробрасывается наружу
        // (не перехватывается catch'ами хендлера и не приводит к повторной записи).
        await WriteAuditAsync(message, cancellationToken);
        return response;
    }

    private async Task WriteAuditAsync(TMessage message, CancellationToken cancellationToken)
    {
        short subjectCeiling;
        int? subjectId = null;
        try
        {
            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            subjectCeiling = access.MaxClassification;

            // Субъект записи (ТБ-030 «кто»): числовой SubjectId — локальный id пользователя (Э3-08);
            // нечисловой (dev-заглушка и т.п.) в колонку субъекта не пишется.
            if (int.TryParse(access.SubjectId, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsedSubject))
            {
                subjectId = parsedSubject;
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Контекст доступа недоступен — фиксируем обращение под МАКСИМАЛЬНЫМ грифом (fail-closed по грифу).
            AuditBehaviorLog.AccessUnavailable(logger, ex, typeof(TMessage).Name);
            subjectCeiling = RestrictedClassificationOnUnknownAccess;
        }

        // Не ниже и допуска субъекта, и объявленного грифа объекта (напр. грифа загружаемого документа).
        var classification = Math.Max(subjectCeiling, message.AuditClassification ?? (short)0);

        await auditWriter.WriteAsync(
            new AuditEntry(message.AuditAction, classification, SubjectId: subjectId,
                PayloadSensitive: message.AuditSummary),
            cancellationToken);
    }
}
