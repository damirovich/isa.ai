namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Тексты правил связи фигуранта (ТФ-ПЕР-06) — общие для валидаторов заведения и правки.</summary>
public static class PersonLinkRules
{
    /// <summary>Поля связи указаны у фигуранта, роль которого не «связь».</summary>
    public const string OnlyForLink = "«Чья связь» и «кем приходится» указываются только у фигуранта с ролью «связь».";
}
