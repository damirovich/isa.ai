using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Risk;

namespace ISC.AI.Profile.Inspector.UI.Demo;

/// <summary>
/// ПОКАЗАТЕЛЬНЫЕ (ДЕМО) данные дашборда — <b>НЕ реальные</b>. Существуют только чтобы экран соответствовал
/// прототипу, пока в системе нет настоящих записей: домен «Нарушение» (<c>inspector.violation</c>,
/// <c>inspector.division</c>) создан, но пуст.
/// </summary>
/// <remarks>
/// ЧЕСТНОСТЬ: везде, где эти данные выводятся, обязателен видимый маркер «ДЕМО» (чип в оболочке + плашка на
/// дашборде). Как только появятся реальные нарушения и справочник подразделений — этот класс удаляется, а
/// дашборд читает данные через сценарии профиля. Уровень риска считает детерминированная формула
/// (<c>RiskScoreCalculator</c>), а не эти константы.
/// </remarks>
public static class InspectorDemoData
{
    /// <summary>Текст обязательной плашки-предупреждения о демо-режиме.</summary>
    public const string Notice =
        "Показательные данные (ДЕМО). Реальных записей пока нет: домен «Нарушение» создан, но не наполнен — "
        + "цифры ниже нужны только для демонстрации вида экрана.";

    /// <summary>Всего проверок (демо).</summary>
    public const int TotalChecks = 7;

    /// <summary>Критических нарушений (демо).</summary>
    public const int CriticalViolations = 2;

    /// <summary>Не устранено (демо).</summary>
    public const int NotRemediated = 1;

    /// <summary>Устранено (демо).</summary>
    public const int Remediated = 3;

    /// <summary>Строка списка «Последние проверки» (демо).</summary>
    /// <param name="Title">Подразделение/объект проверки.</param>
    /// <param name="Date">Дата проверки.</param>
    /// <param name="Location">Территория/расположение.</param>
    /// <param name="Status">Статус устранения.</param>
    public sealed record RecentCheck(string Title, string Date, string Location, string Status);

    /// <summary>Последние проверки (демо).</summary>
    public static IReadOnlyList<RecentCheck> RecentChecks { get; } =
    [
        new("УЗБ", "01.09.2024", "Центр. аппарат", "На контроле"),
        new("УЗК", "15.07.2024", "Центр. аппарат", "Устранено"),
        new("Карасуйский РО", "30.05.2024", "Ошская обл.", "Устранено"),
        new("Токтогульский РО", "05.04.2024", "Джалал-Абадская обл.", "Не устранено"),
    ];

    /// <summary>Строка сводки «Статус устранения» (демо).</summary>
    /// <param name="Label">Наименование статуса.</param>
    /// <param name="Count">Количество.</param>
    /// <param name="Percent">Доля для полосы прогресса, %.</param>
    public sealed record RemediationStat(string Label, int Count, int Percent);

    /// <summary>Статус устранения (демо).</summary>
    public static IReadOnlyList<RemediationStat> Remediation { get; } =
    [
        new("Устранено", 3, 100),
        new("Частично устранено", 1, 35),
        new("На контроле", 2, 65),
        new("Не устранено", 1, 35),
    ];

    /// <summary>Строка таблицы подразделений (демо).</summary>
    /// <param name="Name">Наименование подразделения.</param>
    /// <param name="Note">Регион или расшифровка.</param>
    /// <param name="Checks">Число проверок.</param>
    /// <param name="SubUnits">Число подчинённых районных отделов (для ТУ); 0 — нет/неприменимо.</param>
    public sealed record DivisionStat(string Name, string Note, int Checks, int SubUnits = 0);

    /// <summary>Территориальные управления (демо).</summary>
    public static IReadOnlyList<DivisionStat> Territorial { get; } =
    [
        new("Чуйское управление", "Чуйская обл.", 3, SubUnits: 5),
        new("Ошское управление", "Ошская обл.", 1, SubUnits: 4),
        new("Джалал-Абадское управление", "Джалал-Абадская обл.", 1, SubUnits: 3),
        new("Иссык-Кульское управление", "Иссык-Кульская обл.", 0, SubUnits: 3),
        new("Нарынское управление", "Нарынская обл.", 0, SubUnits: 2),
        new("Таласское управление", "Таласская обл.", 0, SubUnits: 2),
        new("Баткенское управление", "Баткенская обл.", 0, SubUnits: 2),
    ];

    /// <summary>Линейные подразделения (демо).</summary>
    public static IReadOnlyList<DivisionStat> Linear { get; } =
    [
        new("УВКР", "Управление военной контрразведки", 0),
        new("УЗК", "Управление защиты конституционного строя", 1),
        new("УЭБ", "Управление экономической безопасности", 1),
        new("УКР", "Управление контрразведки", 0),
        new("УПТ", "Управление по противодействию терроризму", 0),
        new("УДО", "Управление документального обеспечения", 0),
    ];

    /// <summary>Программа проверки (демо) — модуль «Методики проверок» (§5.2.9).</summary>
    /// <param name="Title">Наименование программы.</param>
    /// <param name="Scope">Объект проверки.</param>
    /// <param name="Duration">Ориентировочный срок.</param>
    /// <param name="Purpose">Цель проверки.</param>
    /// <param name="Questions">Перечень вопросов/пунктов чек-листа.</param>
    public sealed record InspectionProgram(
        string Title, string Scope, string Duration, string Purpose, IReadOnlyList<string> Questions);

    /// <summary>Программы проверок (демо).</summary>
    public static IReadOnlyList<InspectionProgram> Programs { get; } =
    [
        new("Комплексная проверка ТУ", "Территориальные управления", "5 рабочих дней",
            "Оценка оперативно-служебной и организационно-управленческой деятельности территориального управления в полном объёме.",
            [
                "Организация агентурной работы",
                "Ведение документооборота",
                "Исполнение поручений и контрольных сроков",
                "Планирование и отчётность",
                "Взаимодействие с подразделениями",
            ]),
        new("Целевая проверка (агентурная работа)", "Территориальное управление / РО", "2 рабочих дня",
            "Проверка отдельного направления — организации и результативности агентурной работы.",
            ["Полнота учёта", "Качество ведения дел", "Соблюдение режима секретности"]),
        new("Контрольная проверка", "Любое подразделение", "1 рабочий день",
            "Проверка устранения ранее выявленных нарушений.",
            ["Статус устранения по каждому нарушению", "Причины неустранения", "Повторность нарушений"]),
        new("Внеплановая проверка", "Любое подразделение", "По обстановке",
            "Проверка по поручению руководства или по факту происшествия.",
            ["Обстоятельства события", "Действия должностных лиц", "Достаточность принятых мер"]),
    ];

    /// <summary>Методический материал (демо).</summary>
    /// <param name="Title">Наименование.</param>
    /// <param name="Note">Краткое описание.</param>
    public sealed record MethodicMaterial(string Title, string Note);

    /// <summary>Методические материалы (демо).</summary>
    public static IReadOnlyList<MethodicMaterial> Materials { get; } =
    [
        new("Памятка инспектору по оформлению справки", "Структура, обязательные смысловые части, типовые ошибки"),
        new("Порядок фиксации нарушений", "Вид, тяжесть, причина, рекомендация — как заполнять"),
        new("Методика оценки работы подразделения", "Показатели и их источники (СКИД / собственные данные)"),
    ];

    /// <summary>Чек-лист (демо).</summary>
    /// <param name="Title">Наименование.</param>
    /// <param name="Points">Число пунктов.</param>
    public sealed record ChecklistSummary(string Title, int Points);

    /// <summary>Чек-листы (демо).</summary>
    public static IReadOnlyList<ChecklistSummary> Checklists { get; } =
    [
        new("Документооборот подразделения", 12),
        new("Исполнение поручений и сроки", 8),
        new("Режим секретности", 15),
        new("Планирование и отчётность", 9),
    ];

    /// <summary>
    /// Запись реестра рисков (демо). ВАЖНО: демо задаёт только исходные СИГНАЛЫ; сам уровень риска считает
    /// НАСТОЯЩИЙ <see cref="RiskScoreCalculator"/> по формуле Приложения §2 — на экране не нарисованный,
    /// а вычисленный результат.
    /// </summary>
    /// <param name="Division">Подразделение.</param>
    /// <param name="Area">Направление/сфера.</param>
    /// <param name="Description">Существо риска.</param>
    /// <param name="Recommendation">Рекомендация (в рабочей версии — ИИ-черновик с участием человека).</param>
    /// <param name="Signals">Исходные сигналы для формулы риска.</param>
    public sealed record RiskEntry(
        string Division, string Area, string Description, string Recommendation, RiskSignals Signals);

    /// <summary>Реестр рисков (демо-сигналы; уровни вычисляются формулой).</summary>
    public static IReadOnlyList<RiskEntry> Risks { get; } =
    [
        // Σ=16 + 3·1.5 + 2·2 + 1 = 25.5 → высокий
        new("Чуйское управление", "Агентурная работа",
            "Нарушение периодичности контакта с конфидентами — системный характер по 3 из 5 РО.",
            "Вести еженедельный мониторинг контактов. Пересмотреть нагрузку на оперсостав.",
            new RiskSignals(
                [ViolationSeverity.High, ViolationSeverity.High, ViolationSeverity.High,
                 ViolationSeverity.Medium, ViolationSeverity.Medium],
                RepeatCount: 3, OverdueCount: 2, TrendDelta: 1)),

        // Σ=6 + 2·1.5 + 1·2 + 1 = 12 → средний
        new("ДЭБ", "Планирование",
            "Низкое качество аналитических материалов — слабая методологическая основа.",
            "Разработать единый стандарт подготовки аналитики. Ввести внутреннее рецензирование.",
            new RiskSignals(
                [ViolationSeverity.Medium, ViolationSeverity.Medium, ViolationSeverity.Low, ViolationSeverity.Low],
                RepeatCount: 2, OverdueCount: 1, TrendDelta: 1)),

        // Σ=40 + 4·1.5 + 4·2 + 2 = 56 → критический
        new("Джалал-Абадское управление", "Исполнение поручений",
            "Систематическое неисполнение поручений коллегий — 4 из 6 открытых поручений просрочены.",
            "Ввести еженедельный контроль. Рассмотреть вопрос персональной ответственности.",
            new RiskSignals(
                [ViolationSeverity.Critical, ViolationSeverity.Critical, ViolationSeverity.Critical,
                 ViolationSeverity.Critical, ViolationSeverity.High, ViolationSeverity.High],
                RepeatCount: 4, OverdueCount: 4, TrendDelta: 2)),

        // Σ=7 + 4·1.5 + 1·2 + 0 = 15 → средний
        new("Все ТУ", "Документооборот",
            "Разные стандарты оформления документов — затрудняет сравнительный анализ.",
            "Утвердить единые шаблоны. Загрузить в систему как обязательные образцы.",
            new RiskSignals(
                [ViolationSeverity.Medium, ViolationSeverity.Medium, ViolationSeverity.Medium, ViolationSeverity.Low],
                RepeatCount: 4, OverdueCount: 1, TrendDelta: 0)),

        // Σ=2 + 1·1.5 + 0 + 0 = 3.5 → низкий
        new("УЗК", "Взаимодействие",
            "Слабое взаимодействие с территориальными управлениями по оперативным вопросам.",
            "Регламентировать порядок информационного обмена между линейными и ТУ.",
            new RiskSignals(
                [ViolationSeverity.Low, ViolationSeverity.Low],
                RepeatCount: 1, OverdueCount: 0, TrendDelta: 0)),
    ];

    /// <summary>Правило внутреннего контроля (демо).</summary>
    /// <param name="Title">Наименование правила.</param>
    /// <param name="Note">Что проверяет и на каких данных.</param>
    public sealed record ControlRule(string Title, string Note);

    /// <summary>Правила и процедуры внутреннего контроля (демо, §5.2.5.2).</summary>
    public static IReadOnlyList<ControlRule> ControlRules { get; } =
    [
        new("Доля исполненных в срок", "Done с датой ≤ Deadline / всего поручений за период — из поручений СКИД"),
        new("Число просрочек", "Количество Overdue — из поручений и продлений сроков СКИД"),
        new("Скорость устранения", "Средние дни от выявления нарушения до статуса «устранено»"),
        new("Повторяемость", "Доля повторных нарушений того же вида в том же подразделении"),
        new("Динамика нарушений", "Изменение числа нарушений к предыдущему периоду"),
    ];

    /// <summary>Нарушение на мониторинге устранения (демо, §5.2.6).</summary>
    /// <param name="Division">Подразделение.</param>
    /// <param name="Issue">Существо нарушения.</param>
    /// <param name="Deadline">Контрольный срок устранения.</param>
    /// <param name="Status">Статус устранения (доменный перечень профиля).</param>
    /// <param name="Progress">Прогресс устранения, %.</param>
    /// <param name="Comment">Комментарий о ходе устранения.</param>
    public sealed record MonitoringItem(
        string Division, string Issue, string Deadline, RemediationStatus Status, int Progress, string Comment);

    /// <summary>Мониторинг устранения нарушений (демо). Сводки на экране считаются ИЗ этого списка.</summary>
    public static IReadOnlyList<MonitoringItem> Monitoring { get; } =
    [
        new("Иссык-Атинский РО", "Отсутствие контакта с конфидентами", "01.11.2023",
            RemediationStatus.Overdue, 20, "Частично устранено. Контакт восстановлен по 3 из 7 позиций."),
        new("Сокулукский РО", "Формальные отчёты по АОД", "15.12.2023",
            RemediationStatus.Resolved, 100, "Устранено. Новые планы приведены в соответствие с обстановкой."),
        new("Кантский РО", "Слабая работа по лидерам нацменьшинств", "01.04.2024",
            RemediationStatus.UnderControl, 55, "На контроле. Восстановлен контакт с 4 из 7 лидеров."),
        new("Токтогульский РО", "Повторные нарушения по АОД", "01.06.2024",
            RemediationStatus.Overdue, 5, "Не устранено. Руководство РО не приняло мер."),
        new("ДЭБ", "Низкое качество аналитики", "01.06.2024",
            RemediationStatus.UnderControl, 70, "На контроле. Разработаны новые стандарты подготовки материалов."),
        new("УЗК", "Задержка исполнения поручений", "01.10.2024",
            RemediationStatus.Resolved, 100, "Устранено. Введён еженедельный контроль поручений."),
    ];

    /// <summary>Вид находки анализа НПА (демо, §5.2.3.2).</summary>
    public enum FindingKind
    {
        /// <summary>Противоречие/коллизия между нормами.</summary>
        Contradiction,

        /// <summary>Пробел — отсутствие нормы.</summary>
        Gap,

        /// <summary>Рекомендация (напр. актуализировать ссылки).</summary>
        Recommendation,
    }

    /// <summary>Находка анализа НПА (демо).</summary>
    /// <param name="Kind">Вид находки.</param>
    /// <param name="Title">Заголовок.</param>
    /// <param name="Description">Существо находки.</param>
    /// <param name="Sources">Затронутые акты (в рабочей версии — только из извлечённых фрагментов).</param>
    /// <param name="Action">Предлагаемое действие — для проверки человеком.</param>
    public sealed record AnalysisFinding(
        FindingKind Kind, string Title, string Description, IReadOnlyList<string> Sources, string Action);

    /// <summary>Находки анализа НПА (демо). Сводки на экране считаются ИЗ этого списка.</summary>
    public static IReadOnlyList<AnalysisFinding> Findings { get; } =
    [
        new(FindingKind.Contradiction, "Противоречие между Приказом №247 и Инструкцией по АОД",
            "Приказ №247 п.3.4 устанавливает контакт с конфидентами раз в 30 дней, Инструкция разд.5 допускает 45 дней. Требуется унификация.",
            ["Приказ ГКНБ №247", "Инструкция по АОД №312"],
            "Привести Инструкцию в соответствие с Приказом №247"),
        new(FindingKind.Gap, "Пробел: отсутствует норма о порядке хранения материалов ОРМ",
            "Ни один из действующих НПА не регулирует сроки хранения первичных материалов оперативно-розыскных мероприятий.",
            ["Закон КР «Об ОРД»", "Приказ ГКНБ №89"],
            "Разработать отдельный раздел или приказ о порядке хранения"),
        new(FindingKind.Contradiction, "Коллизия: двойное регулирование ответственности за нарушения АОД",
            "Дисциплинарная ответственность за нарушение АОД упоминается в Приказе №312 и в Положении о службе. Критерии и порядок применения различаются.",
            ["Инструкция по АОД №312", "Положение о службе"],
            "Определить приоритетный нормативный акт"),
        new(FindingKind.Gap, "Пробел: отсутствует порядок электронного согласования документов",
            "Действующие НПА не предусматривают порядок согласования служебных документов в электронном виде. Нет правового основания для ЭЦП.",
            ["Инструкция по делопроизводству ГКНБ"],
            "Разработать дополнения к инструкции о делопроизводстве"),
        new(FindingKind.Recommendation, "Рекомендация: актуализировать ссылки на законодательство",
            "Приказ №89 содержит ссылки на Закон КР «Об ОРД» в редакции 2015 года. С тех пор закон претерпел изменения.",
            ["Приказ ГКНБ №89"],
            "Обновить ссылки на действующую редакцию"),
    ];
}
