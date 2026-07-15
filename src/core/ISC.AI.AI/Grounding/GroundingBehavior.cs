using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Grounding;
using Mediator;

namespace ISC.AI.AI.Grounding;

/// <summary>
/// Сквозное поведение Mediator (ТБ-040/041, инж-ТЗ §5.3.1.1): для ГЕНЕРИРУЮЩИХ сценариев, помеченных
/// <see cref="IGroundedScenario"/>, гарантирует, что результат прошёл грунтовку (несёт
/// <see cref="IGroundedResult"/>), а вывод с НЕподтверждёнными ссылками не выдаётся как «готовый» —
/// помечается на уровне конверта.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ «конвейер нельзя обойти» (§5.3.1.1): грунтовку выполняет RAG-оркестратор ядра (единственный
/// путь генерации грунтованного вывода) — это поведение СТРАХУЕТ конвейер: если грунтующий сценарий вернул
/// успех БЕЗ вердикта грунтовки, значит грунтовка обойдена → fail-closed (исключение превращается в
/// неуспешный ответ через ExceptionHandlingBehavior). Ограничение по <see cref="IGroundedScenario"/> —
/// поведение применяется ТОЛЬКО к грунтующим сценариям (прочие его не касаются). Живёт в ядре: профиль
/// не может ослабить (ТБ-041).
/// </remarks>
public sealed class GroundingBehavior<TMessage, TResponse> : IPipelineBehavior<TMessage, TResponse>
    where TMessage : notnull, IMessage, IGroundedScenario
    where TResponse : IResponseDto
{
    /// <inheritdoc />
    public async ValueTask<TResponse> Handle(
        TMessage message, MessageHandlerDelegate<TMessage, TResponse> next, CancellationToken cancellationToken)
    {
        var response = await next(message, cancellationToken);

        // Вердикт проверяем только у УСПЕШНЫХ ответов: неуспех уже сформирован/помечен выше по конвейеру.
        if (!response.Status)
        {
            return response;
        }

        var grounding = (response as IPayloadCarrier)?.Payload as IGroundedResult;
        if (grounding is null)
        {
            // Грунтующий сценарий ОБЯЗАН вернуть результат с вердиктом грунтовки — иначе конвейер обойдён.
            throw new InvalidOperationException(
                $"Сценарий «{typeof(TMessage).Name}» помечен {nameof(IGroundedScenario)}, но результат не несёт "
                + $"вердикта грунтовки ({nameof(IGroundedResult)}). Обход грунтовки запрещён (ТБ-041).");
        }

        // «Не выдаётся как готовый — помечается» (§5.3.1.1): непроверенные ссылки → пометка на конверте,
        // чтобы факт был виден даже потребителю, не читающему полезную нагрузку. Статус остаётся успешным:
        // черновик произведён корректно (HITL), но требует проверки.
        if (!grounding.AllCitationsConfirmed)
        {
            response.StatusMessage =
                "Черновик содержит НЕподтверждённые грунтовкой ссылки — требует проверки человеком (ТБ-040).";
        }

        return response;
    }
}
