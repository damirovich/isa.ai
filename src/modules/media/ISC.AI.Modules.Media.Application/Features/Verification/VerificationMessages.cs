namespace ISC.AI.Modules.Media.Application.Features.Verification;

/// <summary>Тексты отказов верификации, общие для очереди, карточки пары и записи решения.</summary>
public static class VerificationMessages
{
    /// <summary>Отказ стадии руководителя (ТФ-ВЕР-02, ADR-0036): право задаёт профиль (матрица доступа).</summary>
    public const string SupervisorDenied =
        "Итог по расхождению выносит руководитель: право «Верификация — решение при расхождении».";
}
