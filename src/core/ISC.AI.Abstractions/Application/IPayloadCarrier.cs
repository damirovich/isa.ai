namespace ISC.AI.Abstractions.Application;

/// <summary>
/// Доступ к полезной нагрузке конверта без знания её типа — для сквозных поведений Mediator (напр.
/// грунтовка/аудит проверяют нагрузку, не завися от <c>T</c> в <see cref="ResponseDto{T}"/>).
/// </summary>
public interface IPayloadCarrier
{
    /// <summary>Полезная нагрузка ответа (может быть <c>null</c> при неуспехе).</summary>
    object? Payload { get; }
}
