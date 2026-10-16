using System.Text;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>
/// Предложение сопоставления таблиц и столбцов базы Access с полями системы по названиям (ADR-0043 п. 2): «№ задания»,
/// «Фамилия», «Кличка», «Гос. номер»… Это подсказка, а не решение — оператор поправляет её на экране.
/// </summary>
/// <remarks>
/// Названия сравниваются нормализованными: нижний регистр, «ё» как «е», только буквы и цифры («Гос. номер» →
/// «госномер», «№ задания» → «задания»). Точное совпадение важнее вхождения; каждый столбец достаётся одному полю.
/// Двоичные столбцы (объект OLE) предлагаются только в поля фото, текстовые — только в остальные поля. Синонимы
/// включают названия реальной базы отдела (16.10.2026): «место жительство», «гражданство», «Кличка объекта», «Фото» в
/// связях и таблицу «инициатор» (ADR-0043 v0.3).
/// </remarks>
public static class AccessAutoMapper
{
    private const int ExactScore = 100;
    private const int ContainsScore = 10;
    private const int TableNameBonus = 50;

    private static readonly Dictionary<AccessField, string[]> Synonyms = new()
    {
        [AccessField.TaskKey] = ["код", "id", "кодзаписи", "ключ", "кодзадания"],
        [AccessField.TaskNumber] = ["номерзадания", "задания", "nзадания", "номер", "номерзад", "задание"],
        [AccessField.TaskDate] = ["датазадания", "дата", "датапоступления", "датарегистрации", "датаначала"],
        [AccessField.LastName] = ["фамилия", "lastname", "surname"],
        [AccessField.FirstName] = ["имя", "firstname"],
        [AccessField.MiddleName] = ["отчество", "middlename", "patronymic"],
        [AccessField.FullName] = ["фио", "фамилияимяотчество", "фиообъекта"],
        [AccessField.BirthYear] = ["годрождения", "датарождения", "годр", "гр", "рождения"],
        [AccessField.Sex] = ["пол", "sex", "gender"],
        [AccessField.BirthPlace] = ["месторождения"],
        [AccessField.WorkPlace] = ["местоработы", "работа", "работает"],
        [AccessField.Residence] = ["местожительства", "местожительство", "местопроживания", "проживает", "адреспроживания", "адрес"],
        [AccessField.Citizenship] = ["гражданство", "подданство"],
        [AccessField.Photo] = ["фото", "фотография", "photo", "снимок", "изображение"],
        [AccessField.Alias] = ["кличка", "псевдоним", "прозвище"],
        [AccessField.Measure] = ["мероприятие", "видмероприятия"],
        [AccessField.Initiator] = ["инициатор", "гу", "инициаторгу"],
        [AccessField.Justification] = ["обоснование", "основание"],
        [AccessField.Purpose] = ["цельмероприятия", "цель"],
        [AccessField.ObjectInfo] = ["сведенияобобъекте", "сведения", "примечание"],
        [AccessField.Result] = ["результатмероприятия", "результат"],
        [AccessField.LinkKey] = ["код", "id", "кодсвязи", "ключ"],
        [AccessField.LinkTaskNumber] = ["номерзадания", "задания", "nзадания", "кодзадания", "задание"],
        [AccessField.LinkType] = ["связьобъекта", "связь", "видсвязи", "типсвязи", "кемприходится"],
        [AccessField.LinkInfo] = ["сведенияобобъектесвязи", "сведенияосвязи", "сведения", "примечание"],
        [AccessField.LinkName] = ["фиосвязи", "фио", "фамилия"],
        [AccessField.CarMake] = ["маркаавто", "маркаавтомобиля", "марка", "автомобиль"],
        [AccessField.CarPlate] = ["госномер", "гос", "номеравто", "госномеравто", "госзнак"],
        [AccessField.CarOwner] = ["владелец", "владелецавто", "владелецавтомобиля"],
        [AccessField.VisitAddresses] = ["адресапосещения", "местапосещения", "адреса", "адрес"],
        [AccessField.LinkAlias] = ["кличкаобъекта", "кличкасвязи", "кличка", "псевдоним", "прозвище"],
        [AccessField.LinkPhoto] = ["фото", "фотография", "фотосвязи", "photo", "снимок", "изображение"],
        [AccessField.InitiatorTaskNumber] = ["номерзадания", "задания", "nзадания", "задание"],
        [AccessField.InitiatorUnit] = ["инициаторзадания", "инициатор", "управление", "гу", "инициаторгу"],
        [AccessField.InitiatorHead] = ["начуправления", "начальникуправления", "начальник", "руководитель"],
        [AccessField.InitiatorExecutor] = ["исполнитель", "фиоисполнителя", "фиоинициатора"],
        [AccessField.InitiatorDepartment] = ["отдел", "отделинициатора"],
        [AccessField.InitiatorPhone] = ["телефон", "тел", "телефонинициатора", "контактныйтелефон"],
    };

    /// <summary>Нормализованное название: нижний регистр, «ё» → «е», только буквы и цифры.</summary>
    public static string Normalize(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var builder = new StringBuilder(name.Length);
        foreach (var ch in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(ch))
            {
                builder.Append(ch == 'ё' ? 'е' : ch);
            }
        }

        return builder.ToString();
    }

    /// <summary>Предлагает таблицы заданий, связей и инициаторов и столбцы для каждого поля.</summary>
    /// <param name="tables">Таблицы базы.</param>
    public static AccessMapping Suggest(IReadOnlyList<AccessTable> tables)
    {
        ArgumentNullException.ThrowIfNull(tables);

        // Таблица заданий — та, где больше всего полей заданий узнаётся по названиям (и само имя похоже на «задания»);
        // без узнанного № задания или ФИО это не таблица заданий.
        var tasks = tables
            .Select(t => (Table: t, Columns: SuggestColumns(t, AccessFields.TaskFields)))
            .Where(c => c.Columns.ContainsKey(AccessField.TaskNumber) || c.Columns.ContainsKey(AccessField.LastName)
                || c.Columns.ContainsKey(AccessField.FullName))
            .OrderByDescending(c => Score(c.Table, c.Columns, "задани"))
            .FirstOrDefault();
        if (tasks.Table is null)
        {
            return AccessMapping.Empty;
        }

        // Таблица связей — другая, со ссылкой на задание и хотя бы одним полем связи.
        var links = tables
            .Where(t => !ReferenceEquals(t, tasks.Table))
            .Select(t => (Table: t, Columns: SuggestColumns(t, AccessFields.LinkFields)))
            .Where(c => c.Columns.ContainsKey(AccessField.LinkTaskNumber) && c.Columns.Count > 1)
            .OrderByDescending(c => Score(c.Table, c.Columns, "связ"))
            .FirstOrDefault();

        // Таблица инициаторов — третья, со ссылкой на задание и хотя бы одним сведением об инициаторе.
        var initiators = tables
            .Where(t => !ReferenceEquals(t, tasks.Table) && !ReferenceEquals(t, links.Table))
            .Select(t => (Table: t, Columns: SuggestColumns(t, AccessFields.InitiatorFields)))
            .Where(c => c.Columns.ContainsKey(AccessField.InitiatorTaskNumber) && c.Columns.Count > 1)
            .OrderByDescending(c => Score(c.Table, c.Columns, "инициатор"))
            .FirstOrDefault();

        return new AccessMapping(
            tasks.Table.Name,
            tasks.Columns,
            links.Table?.Name,
            links.Table is null ? new Dictionary<AccessField, string>() : links.Columns,
            initiators.Table?.Name,
            initiators.Table is null ? new Dictionary<AccessField, string>() : initiators.Columns);
    }

    private static int Score(AccessTable table, Dictionary<AccessField, string> columns, string nameHint) =>
        columns.Count * ContainsScore + (Normalize(table.Name).Contains(nameHint, StringComparison.Ordinal) ? TableNameBonus : 0);

    /// <summary>Столбцы одной таблицы для указанных полей — когда оператор сам выбрал таблицу.</summary>
    /// <param name="table">Таблица.</param>
    /// <param name="fields">Поля (<see cref="AccessFields.TaskFields"/>, <see cref="AccessFields.LinkFields"/> или <see cref="AccessFields.InitiatorFields"/>).</param>
    public static Dictionary<AccessField, string> SuggestColumns(AccessTable table, IReadOnlyList<AccessField> fields)
    {
        ArgumentNullException.ThrowIfNull(table);
        ArgumentNullException.ThrowIfNull(fields);

        // Все пары (поле, столбец) с оценкой, затем жадно по убыванию: лучшая пара забирает и поле, и столбец.
        var candidates = new List<(AccessField Field, string Column, int Score)>();
        foreach (var field in fields)
        {
            var synonyms = Synonyms[field];
            foreach (var column in table.Columns)
            {
                if (column.IsBinary != field.IsPhoto())
                {
                    continue;
                }

                var normalized = Normalize(column.Name);
                var score = 0;
                for (var i = 0; i < synonyms.Length; i++)
                {
                    if (normalized == synonyms[i])
                    {
                        score = Math.Max(score, ExactScore - i);
                    }
                    else if (synonyms[i].Length >= 4 && normalized.Contains(synonyms[i], StringComparison.Ordinal))
                    {
                        score = Math.Max(score, ContainsScore - Math.Min(i, ContainsScore - 1));
                    }
                }

                if (score > 0)
                {
                    candidates.Add((field, column.Name, score));
                }
            }
        }

        var result = new Dictionary<AccessField, string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (field, column, _) in candidates
            .OrderByDescending(c => c.Score)
            .ThenBy(c => fields.IndexOf(c.Field)))
        {
            if (!result.ContainsKey(field) && used.Add(column))
            {
                result[field] = column;
            }
        }

        return result;
    }

    private static int IndexOf(this IReadOnlyList<AccessField> fields, AccessField field)
    {
        for (var i = 0; i < fields.Count; i++)
        {
            if (fields[i] == field)
            {
                return i;
            }
        }

        return int.MaxValue;
    }
}
