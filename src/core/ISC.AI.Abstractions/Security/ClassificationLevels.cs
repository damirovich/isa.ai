using System.Globalization;

namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Шкала грифов платформы (ADR-0030): пять уровней от «без грифа» до «особой важности». Гриф хранится
/// числом (<see cref="IClassified.Classification"/>, допуск — <see cref="AccessContext.MaxClassification"/>),
/// и решётка доступа сравнивает числа (ТБ-020): больше число — выше гриф. Здесь — ЕДИНСТВЕННЫЙ источник
/// предела шкалы и названий уровней для ядра, пакетов и профилей: форма, валидатор, чип и маркировка
/// документа не должны расходиться.
/// </summary>
/// <remarks>
/// <para>
/// «Без грифа» и «ДСП» — не степени секретности государственных секретов: ДСП — несекретная информация с
/// ограничением распространения. «Секретно», «Совершенно секретно», «Особой важности» — степени
/// секретности государственных секретов по возрастанию.
/// </para>
/// <para>
/// До ADR-0030 шкала была 0–9 без названий. Значения выше <see cref="Max"/> остались лишь в неизменяемом
/// журнале аудита (переписывать его нельзя — цепочка хешей); они трактуются как высший уровень: подпись —
/// «Особой важности», при чтении журнала — видимы допуску <see cref="Max"/> (<c>AuditReader</c>).
/// </para>
/// </remarks>
public static class ClassificationLevels
{
    /// <summary>Без грифа (несекретно): открытая информация; «несекретно» не является степенью секретности.</summary>
    public const short Unclassified = 0;

    /// <summary>ДСП — «Для служебного пользования»: несекретная информация с ограничением распространения.</summary>
    public const short ForOfficialUse = 1;

    /// <summary>Секретно — степень секретности государственных секретов.</summary>
    public const short Secret = 2;

    /// <summary>Совершенно секретно — более высокая степень секретности.</summary>
    public const short TopSecret = 3;

    /// <summary>Особой важности — наивысшая степень секретности.</summary>
    public const short SpecialImportance = 4;

    /// <summary>Верхний предел шкалы: гриф и допуск задаются в пределах 0..<see cref="Max"/>.</summary>
    public const short Max = SpecialImportance;

    /// <summary>Все уровни шкалы по возрастанию — для списков выбора.</summary>
    public static IReadOnlyList<short> All { get; } = [Unclassified, ForOfficialUse, Secret, TopSecret, SpecialImportance];

    /// <summary>Уровень в пределах шкалы.</summary>
    public static bool IsValid(short level) => level is >= Unclassified and <= Max;

    /// <summary>Уровень для показа: значение выше <see cref="Max"/> (прежняя шкала, журнал аудита) — высший уровень.</summary>
    public static short Normalize(short level) => level > Max ? Max : level;

    /// <summary>Название уровня для интерфейса: «Без грифа», «ДСП», «Секретно», «Совершенно секретно», «Особой важности».</summary>
    public static string Label(short level) => Normalize(level) switch
    {
        Unclassified => "Без грифа",
        ForOfficialUse => "ДСП",
        Secret => "Секретно",
        TopSecret => "Совершенно секретно",
        SpecialImportance => "Особой важности",
        var other => "гриф " + other.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>
    /// Маркировка на документе и при выдаче (ТБ-033): «НЕСЕКРЕТНО», «ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ», «СЕКРЕТНО»,
    /// «СОВЕРШЕННО СЕКРЕТНО», «ОСОБОЙ ВАЖНОСТИ».
    /// </summary>
    public static string Marking(short level) => Normalize(level) switch
    {
        Unclassified => "НЕСЕКРЕТНО",
        ForOfficialUse => "ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ",
        Secret => "СЕКРЕТНО",
        TopSecret => "СОВЕРШЕННО СЕКРЕТНО",
        SpecialImportance => "ОСОБОЙ ВАЖНОСТИ",
        var other => "ГРИФ " + other.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary>Пояснение уровня (подсказка в форме и чипе).</summary>
    public static string Description(short level) => Normalize(level) switch
    {
        Unclassified => "Открытая информация; «несекретно» не является степенью государственной секретности",
        ForOfficialUse => "«Для служебного пользования»: несекретная информация с ограничением распространения",
        Secret => "Степень секретности государственных секретов",
        TopSecret => "Более высокая степень секретности государственных секретов",
        SpecialImportance => "Наивысшая степень секретности государственных секретов",
        _ => "Уровень вне шкалы",
    };

    /// <summary>Уровень — государственный секрет («Секретно» и выше), а не только ограничение распространения.</summary>
    public static bool IsStateSecret(short level) => level >= Secret;
}
