using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Inspector.Application.Common;

/// <summary>
/// Маркировка грифа для профиля «Инспектор»: числовой гриф ядра → текст на документе. Названия уровней —
/// единая шкала платформы (ADR-0030, <see cref="ClassificationLevels"/>), чтобы экспорт «Инспектора» и отчёты
/// документооборота маркировали один и тот же гриф одинаково.
/// </summary>
public static class ClassificationMarking
{
    /// <summary>
    /// Текстовая маркировка по числовому грифу: «НЕСЕКРЕТНО», «ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ», «СЕКРЕТНО»,
    /// «СОВЕРШЕННО СЕКРЕТНО», «ОСОБОЙ ВАЖНОСТИ».
    /// </summary>
    public static string For(short classification) => ClassificationLevels.Marking(classification);
}
