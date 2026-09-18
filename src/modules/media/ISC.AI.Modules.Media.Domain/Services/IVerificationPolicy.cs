using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// ПОРТ ПРОФИЛЯ: кто вправе выступать на стадии верификации (ТП-004: эксперт по лицам / верификатор).
/// Само правило двух лиц (<see cref="TwoPersonRule"/>) профилю НЕ принадлежит и им не ослабляется —
/// профиль отвечает только на вопрос о роли субъекта.
/// </summary>
public interface IVerificationPolicy
{
    /// <summary>Вправе ли пользователь записать решение указанной стадии.</summary>
    Task<bool> CanActAsync(VerificationStage stage, int userId, CancellationToken cancellationToken = default);
}
