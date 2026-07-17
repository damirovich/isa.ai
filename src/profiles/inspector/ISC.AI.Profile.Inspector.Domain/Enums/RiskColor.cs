namespace ISC.AI.Profile.Inspector.Domain.Enums;

/// <summary>Цвет светофора дашборда (Приложение §2): свёртка 4 уровней риска в 3 цвета.</summary>
public enum RiskColor
{
    /// <summary>Зелёный — низкий риск.</summary>
    Green = 0,

    /// <summary>Жёлтый — средний риск.</summary>
    Yellow = 1,

    /// <summary>Красный — высокий или критический риск.</summary>
    Red = 2,
}
