namespace ISC.AI.Modules.DocFlow.Domain.Enums;

/// <summary>Язык файла документа (ТЗ СКИД §7.1 — двуязычие рус/кырг).</summary>
public enum DocumentLanguage
{
    /// <summary>Не указан.</summary>
    Unspecified = 0,

    /// <summary>Русский.</summary>
    Russian = 1,

    /// <summary>Кыргызский.</summary>
    Kyrgyz = 2,
}
