namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>
/// Вид комментария к документу (ТЗ СКИД §4.8) — классификационная метка для UI (цвет чипа);
/// бизнес-логика от значения НЕ зависит (перенос поведения СКИД 1:1).
/// </summary>
public enum CommentType
{
    /// <summary>Вопрос.</summary>
    Question = 1,

    /// <summary>Замечание.</summary>
    Remark = 2,

    /// <summary>Согласование.</summary>
    Approval = 3,
}
