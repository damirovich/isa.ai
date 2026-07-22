using ISC.AI.Abstractions.Enums;

namespace ISC.AI.Abstractions.AI;

/// <summary>
/// Сервер инференса роли <see cref="Role"/> недоступен — исчерпаны повторы или сработал circuit breaker
/// (ТН-003, ТНД-001: «сбой одного фонового процесса не должен приводить к отказу интерактивной части»).
/// Управляемая деградация: вызывающая сторона обязана ловить это отдельно от прочих ошибок и показать
/// понятное сообщение, а не дать необработанному исключению завалить интерактивную часть.
/// </summary>
public sealed class ModelUnavailableException(ModelRole role, Exception? innerException)
    : Exception($"Сервер модели роли «{role}» недоступен.", innerException)
{
    /// <summary>Роль модели, сервер которой недоступен.</summary>
    public ModelRole Role { get; } = role;
}
