using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ISC.AI.Profile.Investigation.Application.Features.Cases;
using ISC.AI.Profile.Investigation.Application.Features.Persons;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>Разбор значений полей Access в значения системы: текст, даты, год рождения, пол.</summary>
internal static partial class AccessValues
{
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");

    private static readonly string[] DateFormats =
        ["dd.MM.yyyy", "d.M.yyyy", "dd.MM.yy", "d.M.yy", "yyyy-MM-dd", "dd/MM/yyyy", "d/M/yyyy", "dd.MM.yyyy H:mm:ss"];

    /// <summary>Текст поля: строка без краевых пробелов, число без «.0», дата «дд.мм.гггг»; пустое и двоичное — нет.</summary>
    public static string? Text(object? value) => value switch
    {
        null or byte[] => null,
        string text => string.IsNullOrWhiteSpace(text) ? null : text.Trim(),
        DateTime date => date.ToString(date.TimeOfDay == TimeSpan.Zero ? "dd.MM.yyyy" : "dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture),
        double number when number == Math.Floor(number) && Math.Abs(number) < 1e15 => ((long)number).ToString(CultureInfo.InvariantCulture),
        float number when number == MathF.Floor(number) && Math.Abs(number) < 1e7 => ((long)number).ToString(CultureInfo.InvariantCulture),
        decimal number => number.ToString("0.############", CultureInfo.InvariantCulture),
        bool flag => flag ? "да" : "нет",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() is { } text && !string.IsNullOrWhiteSpace(text) ? text.Trim() : null,
    };

    /// <summary>Дата поля: значение «Дата/время» или строка в привычном формате.</summary>
    public static DateOnly? Date(object? value) => value switch
    {
        DateTime date => DateOnly.FromDateTime(date),
        string text when DateTime.TryParseExact(text.Trim(), DateFormats, Russian, DateTimeStyles.None, out var parsed)
            => DateOnly.FromDateTime(parsed),
        _ => null,
    };

    /// <summary>
    /// Год или дата рождения: «Дата/время» — дата; число или строка с четырьмя цифрами («1975», «1975 г.р.») — год;
    /// строка-дата — дата. Неправдоподобное (раньше 1900 или в будущем) — не распознано.
    /// </summary>
    public static (DateOnly? Date, int? Year) Birth(object? value, DateOnly today)
    {
        var date = Date(value);
        if (date is { } known)
        {
            return known.Year >= PersonQuestionnaireValidator.MinBirthYear && known <= today ? (known, known.Year) : (null, null);
        }

        int? year = value switch
        {
            int number => number,
            short number => number,
            long number when number is > 0 and < 10000 => (int)number,
            double number when number == Math.Floor(number) && number is > 0 and < 10000 => (int)number,
            decimal number when number == decimal.Floor(number) && number is > 0 and < 10000 => (int)number,
            string text when YearPattern().Match(text) is { Success: true } match
                => int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture),
            _ => null,
        };
        return year is { } y && y >= PersonQuestionnaireValidator.MinBirthYear && y <= today.Year ? (null, y) : (null, null);
    }

    /// <summary>Пол по первой букве («м», «муж.», «M» — мужской; «ж», «жен.», «F» — женский).</summary>
    public static PersonSex? Sex(string? text)
    {
        var normalized = AccessAutoMapper.Normalize(text);
        return normalized.Length == 0 ? null : normalized[0] switch
        {
            'м' or 'm' => PersonSex.Male,
            'ж' or 'f' or 'w' => PersonSex.Female,
            _ => null,
        };
    }

    /// <summary>Обрезает до предела с многоточием.</summary>
    public static string Fit(string value, int max) => value.Length <= max ? value : string.Concat(value.AsSpan(0, max - 1), "…");

    /// <summary>
    /// Значение для поля с пределом длины: не влезает — в поле обрезанное, а полностью — строкой в примечание, чтобы
    /// ничего из базы не потерялось.
    /// </summary>
    public static string? FitField(string? value, int max, string label, List<string> notes)
    {
        if (value is null || value.Length <= max)
        {
            return value;
        }

        notes.Add($"{label} (полностью): {value}");
        return Fit(value, max);
    }

    /// <summary>
    /// Строки примечания одним текстом в пределах поля. Не влезло — обрезается, а в <paramref name="warnings"/> идёт
    /// предупреждение для отчёта: обрезка не должна быть молчаливой.
    /// </summary>
    public static string? JoinNotes(List<string> lines, int max, string what, List<string> warnings)
    {
        if (lines.Count == 0)
        {
            return null;
        }

        var text = string.Join('\n', lines);
        if (text.Length > max)
        {
            warnings.Add($"{what} длиннее {max} знаков ({text.Length}) — сохранено обрезанным, полный текст — в исходной базе");
        }

        return Fit(text, max);
    }

    /// <summary>
    /// Нормализованный ключ записи справочника: регистр, «ё», пробелы и знаки не различаются — «6ГУ», «6 ГУ» и «6-ГУ»
    /// одна запись (в реальной базе отдела инициатор записан «6ГУ» и «6Гу», ADR-0043 v0.3).
    /// </summary>
    public static string ReferenceKey(string name) => AccessAutoMapper.Normalize(name);

    // Год — ровно четыре цифры, вокруг не цифры: «1975», «1975 г.р.», «г.р. 1975».
    [GeneratedRegex(@"^\D*(\d{4})\D*$")]
    private static partial Regex YearPattern();
}

/// <summary>Задание из базы, разобранное в значения системы.</summary>
internal sealed class AccessTaskData
{
    public required int Ordinal { get; init; }

    public required string Key { get; init; }

    /// <summary>Значение столбца «код записи» как в базе (сверка второго чтения — фото).</summary>
    public string? KeyText { get; init; }

    /// <summary>№ задания как в базе (реквизит задания); <see langword="null"/> — номера нет.</summary>
    public string? Number { get; init; }

    /// <summary>№ дела: № задания, «ACCESS-код» без номера, «№ (запись код)» при повторе № в базе.</summary>
    public required string CaseNumber { get; set; }

    public DateOnly? Date { get; init; }

    public string? DisplayName { get; init; }

    public PersonQuestionnaire Questionnaire { get; init; } = PersonQuestionnaire.Empty;

    /// <summary>Примечание объекта — собирается <see cref="CompleteNotes"/> после всех привязок.</summary>
    public string? PersonNotes { get; private set; }

    /// <summary>Инициатор (ГУ) — из задания или таблицы инициаторов (она точнее).</summary>
    public string? Initiator { get; set; }

    /// <summary>ФИО инициатора (исполнитель у инициатора).</summary>
    public string? InitiatorName { get; set; }

    /// <summary>Телефон инициатора.</summary>
    public string? InitiatorPhone { get; set; }

    /// <summary>Прочие реквизиты инициатора (начальник управления, отдел).</summary>
    public string? InitiatorDetails { get; set; }

    /// <summary>К заданию найдена строка таблицы инициаторов.</summary>
    public bool HasInitiatorRow { get; set; }

    public required string Justification { get; init; }

    public required string Purpose { get; init; }

    /// <summary>Примечание задания — собирается <see cref="CompleteNotes"/> после всех привязок.</summary>
    public string? TaskNotes { get; private set; }

    /// <summary>Пустая строка (все сопоставленные поля пусты): не переносится и ошибкой не считается.</summary>
    public bool IsEmpty { get; init; }

    public List<AccessLinkData> Links { get; } = [];

    /// <summary>Строки примечания объекта до сборки: сведения, которых нет больше нигде.</summary>
    public List<string> PersonNoteLines { get; } = [];

    /// <summary>Полные значения полей объекта, обрезанных до предела поля («… (полностью)»).</summary>
    public List<string> PersonOverflowLines { get; } = [];

    /// <summary>Короткие служебные строки примечания задания: повтор №, расхождение инициатора.</summary>
    public List<string> TaskHeadLines { get; } = [];

    /// <summary>Строки примечания задания до сборки: мероприятие, дата, результат.</summary>
    public List<string> TaskNoteLines { get; } = [];

    /// <summary>Полные значения реквизитов задания, обрезанных до предела поля («… (полностью)»).</summary>
    public List<string> TaskOverflowLines { get; } = [];

    /// <summary>Что перенесётся не полностью или не так, как в базе, — в отчёт; запись при этом переносится.</summary>
    public List<string> Warnings { get; } = [];

    /// <summary>Почему запись не переносится; <see langword="null"/> — переносится.</summary>
    public string? Problem { get; set; }

    /// <summary>Как назвать строку базы человеку: «записи 3» (по коду) или «строки 5» (кода нет).</summary>
    public string RowLabel => KeyText is null ? "строки " + Ordinal.ToString(CultureInfo.InvariantCulture) : "записи " + KeyText;

    /// <summary>
    /// Собирает примечания объекта и задания в пределах полей. Порядок — от незаменимого к повторам: откуда перенесено,
    /// служебные пометки, сведения, и только в конце полные копии значений, уже лежащих в полях обрезанными, — при
    /// обрезке примечания теряется повтор, а не единственная копия сведений. Обрезка не молчаливая — предупреждение в
    /// <see cref="Warnings"/>.
    /// </summary>
    public void CompleteNotes(string fileName, string tasksTable)
    {
        var provenance = $"Перенесено из базы Access «{fileName}», таблица «{tasksTable}», код записи {Key}.";
        PersonNotes = AccessValues.JoinNotes([.. PersonNoteLines, .. PersonOverflowLines], 4000, "примечание объекта", Warnings);
        TaskNotes = AccessValues.JoinNotes(
            [provenance, .. TaskHeadLines, .. TaskNoteLines, .. TaskOverflowLines], TaskRequisitesValidator.MaxNotesLength, "примечание задания", Warnings);
    }
}

/// <summary>Связь из базы, разобранная в значения системы.</summary>
internal sealed class AccessLinkData
{
    public required int Ordinal { get; init; }

    public required string Key { get; init; }

    /// <summary>Значение столбца «код связи» как в базе (сверка второго чтения — фото).</summary>
    public string? KeyText { get; init; }

    public string? TaskRef { get; init; }

    public string? LinkType { get; init; }

    public string? Name { get; init; }

    /// <summary>Кличка объекта связи — в анкету связи.</summary>
    public string? Alias { get; init; }

    /// <summary>«Сведения об объекте связи» — в ориентировку анкеты связи (ADR-0055).</summary>
    public string? Orientation { get; init; }

    public string? Notes { get; init; }

    public string? CarMake { get; init; }

    public string? CarPlate { get; init; }

    public string? CarNotes { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Пустая строка (все сопоставленные поля пусты, фото тоже): не переносится и ошибкой не считается.</summary>
    public bool IsEmpty { get; init; }

    /// <summary>В строке есть фото (сами байты первым чтением не хранятся — их берёт второе чтение).</summary>
    public bool HasPhoto { get; init; }

    /// <summary>Что перенесётся не полностью (обрезка примечаний) — в отчёт, запись при этом переносится.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public AccessTaskData? Task { get; set; }

    public string? Problem { get; set; }
}

/// <summary>
/// Содержимое базы, разобранное по сопоставлению (без фото): задания, связи и сведения об инициаторах и их привязка к
/// заданиям. Одинаково строится проверкой и переносом — что показала проверка, то и переносится.
/// </summary>
internal sealed class AccessSnapshot
{
    /// <summary>Заменитель пустого обязательного реквизита задания (ADR-0043 п. 5).</summary>
    public const string NotSpecified = "не указано в исходной базе";

    /// <summary>Длина названия инициатора (ГУ) — записи справочника; длиннее — полностью в примечание задания.</summary>
    public const int MaxInitiatorLength = 300;

    private AccessSnapshot(IReadOnlyList<AccessTaskData> tasks, IReadOnlyList<AccessLinkData> links, InitiatorRows initiators)
    {
        Tasks = tasks;
        Links = links;
        InitiatorProblems = initiators.Problems;
        EmptyInitiatorRows = initiators.Empty;
    }

    /// <summary>Задания в порядке чтения (с пустыми строками — порядок второго чтения, фото, тот же).</summary>
    public IReadOnlyList<AccessTaskData> Tasks { get; }

    /// <summary>Связи в порядке чтения (с пустыми строками).</summary>
    public IReadOnlyList<AccessLinkData> Links { get; }

    /// <summary>Строки таблицы инициаторов, которые не применены, — с причиной (ключ — № задания или порядковый номер).</summary>
    public IReadOnlyList<(string Key, string Message)> InitiatorProblems { get; }

    /// <summary>Пустых строк таблицы инициаторов (нет ни одного сведения).</summary>
    public int EmptyInitiatorRows { get; }

    /// <summary>Пустых строк заданий, связей и инициаторов, которые пропускаются (без ошибок).</summary>
    public int EmptyRows =>
        Tasks.Count(t => t.IsEmpty && t.Problem is null) + Links.Count(l => l.IsEmpty && l.Problem is null) + EmptyInitiatorRows;

    /// <summary>Порядок строк заданий: по коду записи, если он сопоставлен (проверка и перенос читают одинаково).</summary>
    public static string? TaskOrder(AccessMapping mapping) => mapping.TaskColumns.GetValueOrDefault(AccessField.TaskKey);

    /// <summary>Порядок строк связей: по коду связи, если он сопоставлен (первое чтение и чтение фото — одинаково).</summary>
    public static string? LinkOrder(AccessMapping mapping) => mapping.LinkColumns.GetValueOrDefault(AccessField.LinkKey);

    /// <summary>Ошибка сопоставления или <see langword="null"/>, если по нему можно читать.</summary>
    public static string? Validate(IReadOnlyList<AccessTable> tables, AccessMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(tables);
        ArgumentNullException.ThrowIfNull(mapping);

        if (string.IsNullOrWhiteSpace(mapping.TasksTable))
        {
            return "Выберите таблицу заданий.";
        }

        var error = ValidateTable(tables, mapping.TasksTable, mapping.TaskColumns, AccessFields.TaskFields);
        if (error is not null)
        {
            return error;
        }

        if (!mapping.TaskColumns.ContainsKey(AccessField.LastName) && !mapping.TaskColumns.ContainsKey(AccessField.FullName))
        {
            return "Укажите столбец фамилии или ФИО объекта.";
        }

        if (mapping.LinksTable is not null)
        {
            if (string.Equals(mapping.LinksTable, mapping.TasksTable, StringComparison.Ordinal))
            {
                return "Таблица связей должна быть другой, не таблицей заданий.";
            }

            error = ValidateTable(tables, mapping.LinksTable, mapping.LinkColumns, AccessFields.LinkFields);
            if (error is not null)
            {
                return error;
            }

            if (!mapping.LinkColumns.ContainsKey(AccessField.LinkTaskNumber))
            {
                return "Укажите в таблице связей столбец № задания — без него связь не к чему привязать.";
            }
        }

        if (mapping.InitiatorsTable is null)
        {
            return null;
        }

        if (string.Equals(mapping.InitiatorsTable, mapping.TasksTable, StringComparison.Ordinal)
            || string.Equals(mapping.InitiatorsTable, mapping.LinksTable, StringComparison.Ordinal))
        {
            return "Таблица инициаторов должна быть отдельной — не таблицей заданий и не таблицей связей.";
        }

        error = ValidateTable(tables, mapping.InitiatorsTable, mapping.InitiatorColumns, AccessFields.InitiatorFields);
        if (error is not null)
        {
            return error;
        }

        return mapping.InitiatorColumns.ContainsKey(AccessField.InitiatorTaskNumber)
            ? null
            : "Укажите в таблице инициаторов столбец № задания — без него сведения не к чему привязать.";
    }

    /// <summary>Отпечаток «файл + сопоставление + выбор оператора»: запуск сверяет его с проверкой.</summary>
    public static string Fingerprint(string sha256, AccessMapping mapping, AccessImportOptions options)
    {
        var text = new StringBuilder()
            .Append(sha256).Append('|').Append(mapping.TasksTable).Append('|').Append(mapping.LinksTable).Append('|')
            .Append(mapping.InitiatorsTable).Append('|')
            .AppendJoin(';', mapping.TaskColumns.OrderBy(c => c.Key).Select(c => $"{(int)c.Key}={c.Value}")).Append('|')
            .AppendJoin(';', mapping.LinkColumns.OrderBy(c => c.Key).Select(c => $"{(int)c.Key}={c.Value}")).Append('|')
            .AppendJoin(';', mapping.InitiatorColumns.OrderBy(c => c.Key).Select(c => $"{(int)c.Key}={c.Value}")).Append('|')
            .Append(CultureInfo.InvariantCulture, $"{options.DivisionId}|{options.Classification}|{(int)options.Status}|{options.CreateMissingReferences}")
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Читает и разбирает задания, связи и сведения об инициаторах.</summary>
    /// <param name="database">Открытая база.</param>
    /// <param name="mapping">Сопоставление (уже проверенное <see cref="Validate"/>).</param>
    /// <param name="fileName">Имя файла базы — для примечания «откуда перенесено».</param>
    /// <param name="today">Сегодня (дата задания без даты в базе, правдоподобие года рождения).</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<AccessSnapshot> LoadAsync(
        IAccessDatabase database, AccessMapping mapping, string fileName, DateOnly today, CancellationToken cancellationToken)
    {
        var tasks = new List<AccessTaskData>();
        var taskColumns = mapping.TaskColumns.Where(c => !c.Key.IsPhoto()).Select(c => c.Value).ToList();
        var numbers = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var ordinal = 0;
        await foreach (var row in database.ReadRowsAsync(mapping.TasksTable!, taskColumns, TaskOrder(mapping), cancellationToken))
        {
            tasks.Add(ParseTask(++ordinal, row, mapping, today, numbers));
        }

        var ambiguous = MarkDuplicateTasks(tasks);

        var initiators = InitiatorRows.None;
        if (mapping.InitiatorsTable is not null)
        {
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            await foreach (var row in database.ReadRowsAsync(
                mapping.InitiatorsTable, mapping.InitiatorColumns.Values.ToList(), null, cancellationToken))
            {
                rows.Add(row);
            }

            initiators = AttachInitiators(tasks, rows, mapping, ambiguous);
        }

        var links = new List<AccessLinkData>();
        if (mapping.LinksTable is not null)
        {
            // Фото связи читается и здесь, но только чтобы знать, есть ли оно (строка «№ задания + фото» — связь, а не
            // пустая запись): строки идут по одной, байты не хранятся. В материалы фото берёт второе чтение.
            var linkColumns = mapping.LinkColumns.Select(c => c.Value).ToList();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            ordinal = 0;
            await foreach (var row in database.ReadRowsAsync(mapping.LinksTable, linkColumns, LinkOrder(mapping), cancellationToken))
            {
                links.Add(ParseLink(++ordinal, row, mapping, seen));
            }

            MarkDuplicateLinks(links);
            AttachLinks(tasks, links, ambiguous);
        }

        foreach (var task in tasks.Where(t => !t.IsEmpty))
        {
            task.CompleteNotes(fileName, mapping.TasksTable!);
        }

        return new AccessSnapshot(tasks, links, initiators);
    }

    private static string? ValidateTable(
        IReadOnlyList<AccessTable> tables, string tableName, IReadOnlyDictionary<AccessField, string> columns,
        IReadOnlyList<AccessField> allowed)
    {
        var table = tables.FirstOrDefault(t => string.Equals(t.Name, tableName, StringComparison.Ordinal));
        if (table is null)
        {
            return $"В базе нет таблицы «{tableName}».";
        }

        var known = table.Columns.Select(c => c.Name).ToHashSet(StringComparer.Ordinal);
        foreach (var (field, column) in columns)
        {
            if (!allowed.Contains(field))
            {
                return $"Поле «{field.Label()}» не относится к таблице «{tableName}».";
            }

            if (!known.Contains(column))
            {
                return $"В таблице «{tableName}» нет столбца «{column}».";
            }
        }

        var reused = columns.GroupBy(c => c.Value, StringComparer.Ordinal).FirstOrDefault(g => g.Count() > 1);
        return reused is null ? null : $"Столбец «{reused.Key}» выбран для нескольких полей сразу.";
    }

    // Строка пуста, если пусты все сопоставленные поля, кроме кода записи и фото (фото первым чтением не читается —
    // строку с одним лишь фото узнаёт второе чтение и отправляет в отчёт).
    private static bool IsEmptyRow(
        IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<AccessField, string> columns, AccessField keyField) =>
        columns.Where(c => c.Key != keyField && !c.Key.IsPhoto()).All(c => AccessValues.Text(row.GetValueOrDefault(c.Value)) is null);

    private static AccessTaskData ParseTask(
        int ordinal, IReadOnlyDictionary<string, object?> row, AccessMapping mapping, DateOnly today, Dictionary<string, int> numbers)
    {
        object? Value(AccessField field) =>
            mapping.TaskColumns.TryGetValue(field, out var column) ? row.GetValueOrDefault(column) : null;
        string? Text(AccessField field) => AccessValues.Text(Value(field));

        // Ключ записи — код записи; без столбца кода — № задания, а повтор № получает счётчик («1/#2»): иначе второе
        // задание с тем же № считалось бы той же строкой и терялось вопреки решению «переносятся обе» (16.10.2026).
        var number = Text(AccessField.TaskNumber);
        var keyText = Text(AccessField.TaskKey);
        string key;
        if (keyText is not null)
        {
            key = keyText;
        }
        else if (number is not null)
        {
            var seen = numbers[number] = numbers.GetValueOrDefault(number) + 1;
            key = seen == 1 ? number : number + "#" + seen.ToString(CultureInfo.InvariantCulture);
        }
        else
        {
            key = "#" + ordinal.ToString(CultureInfo.InvariantCulture);
        }

        // Пустая строка (в Access остаётся от начатой и брошенной записи формы) — не задание и не ошибка: число видно в
        // проверке. Строку, в которой есть только фото, второе чтение отправит в отчёт.
        if (IsEmptyRow(row, mapping.TaskColumns, AccessField.TaskKey))
        {
            return new AccessTaskData
            {
                Ordinal = ordinal,
                Key = key,
                KeyText = keyText,
                CaseNumber = "ACCESS-" + key,
                Justification = NotSpecified,
                Purpose = NotSpecified,
                IsEmpty = true,
            };
        }

        string? problem = null;
        var personNotes = new List<string>();
        var personOverflow = new List<string>();
        var taskNotes = new List<string>();
        var taskOverflow = new List<string>();

        // Объект: ФИО одним полем или из частей; без имени запись не переносится.
        var fullName = Text(AccessField.FullName)
            ?? string.Join(' ', new[] { Text(AccessField.LastName), Text(AccessField.FirstName), Text(AccessField.MiddleName) }
                .Where(part => part is not null));
        var displayName = AccessValues.FitField(string.IsNullOrWhiteSpace(fullName) ? null : fullName, 500, "ФИО", personOverflow);
        if (displayName is null)
        {
            problem = "нет ФИО объекта — запись не переносится";
        }

        var caseNumber = number ?? "ACCESS-" + key;
        if (caseNumber.Length > CreateCaseValidator.MaxNumberLength)
        {
            problem ??= $"№ задания длиннее {CreateCaseValidator.MaxNumberLength} знаков";
        }

        var birthRaw = Value(AccessField.BirthYear);
        var (birthDate, birthYear) = AccessValues.Birth(birthRaw, today);
        if (birthDate is null && birthYear is null && AccessValues.Text(birthRaw) is { } birthText)
        {
            personNotes.Add($"Год (дата) рождения в базе: «{birthText}» — не распознан");
        }

        var sexText = Text(AccessField.Sex);
        var sex = AccessValues.Sex(sexText);
        if (sex is null && sexText is not null)
        {
            personNotes.Add($"Пол в базе: «{sexText}»");
        }

        // Гражданство и «сведения об объекте» — в поля анкеты (ТФ-ПЕР-13, ADR-0055), а не в примечание, как было до
        // появления этих полей; не влезло — обрезанное в поле, полностью — в примечание.
        var questionnaire = new PersonQuestionnaire(
            birthDate,
            birthYear,
            AccessValues.FitField(Text(AccessField.BirthPlace), PersonQuestionnaireValidator.MaxPlaceLength, "Место рождения", personOverflow),
            AccessValues.FitField(Text(AccessField.WorkPlace), PersonQuestionnaireValidator.MaxPlaceLength, "Место работы", personOverflow),
            AccessValues.FitField(Text(AccessField.Residence), PersonQuestionnaireValidator.MaxResidenceLength, "Место жительства", personOverflow),
            sex,
            AccessValues.FitField(Text(AccessField.Alias), PersonQuestionnaireValidator.MaxAliasLength, "Кличка", personOverflow),
            AccessValues.FitField(Text(AccessField.Citizenship), PersonQuestionnaireValidator.MaxCitizenshipLength, "Гражданство", personOverflow),
            AccessValues.FitField(Text(AccessField.ObjectInfo), PersonQuestionnaireValidator.MaxOrientationLength, "Сведения об объекте", personOverflow));

        // Задание: пустые обязательные реквизиты — пометкой, а не выдумкой (ADR-0043 п. 5). Даты нет (в реальной базе
        // отдела такого столбца нет вовсе) — дело открывается датой переноса, и это сказано в примечании.
        var dateValue = Value(AccessField.TaskDate);
        var date = AccessValues.Date(dateValue);
        if (date is null && AccessValues.Text(dateValue) is { } dateText)
        {
            taskNotes.Add($"Дата задания в базе: «{dateText}» — не распознана, дело открыто датой переноса");
        }
        else if (date is null)
        {
            taskNotes.Add("Даты задания в базе нет — дело открыто датой переноса");
        }

        var justification = AccessValues.FitField(Text(AccessField.Justification), TaskRequisitesValidator.MaxJustificationLength, "Обоснование", taskOverflow);
        var purpose = AccessValues.FitField(Text(AccessField.Purpose), TaskRequisitesValidator.MaxPurposeLength, "Цель мероприятия", taskOverflow);
        if (Text(AccessField.Measure) is { } measure)
        {
            taskNotes.Insert(0, "Мероприятие: " + measure);
        }

        if (Text(AccessField.Result) is { } result)
        {
            taskNotes.Add("Результат мероприятия: " + result);
        }

        var task = new AccessTaskData
        {
            Ordinal = ordinal,
            Key = key,
            KeyText = keyText,
            Number = number,
            CaseNumber = caseNumber,
            Date = date,
            DisplayName = displayName,
            Questionnaire = questionnaire,
            Initiator = AccessValues.FitField(Text(AccessField.Initiator), MaxInitiatorLength, "Инициатор", taskOverflow),
            Justification = justification ?? NotSpecified,
            Purpose = purpose ?? NotSpecified,
            Problem = problem,
        };
        task.PersonNoteLines.AddRange(personNotes);
        task.PersonOverflowLines.AddRange(personOverflow);
        task.TaskNoteLines.AddRange(taskNotes);
        task.TaskOverflowLines.AddRange(taskOverflow);
        return task;
    }

    /// <summary>
    /// Повтор кода записи — отказ второй записи (это одна строка дважды). Повтор № задания — обе переносятся (решение
    /// заказчика 16.10.2026): первая переносимая сохраняет № дела, следующие получают «№ (запись код)» («№ (строка N)»
    /// без столбца кода) с пометкой и предупреждением. № неоднозначен, если встречается в базе больше одного раза у любых
    /// непустых строк — и у отказных тоже (связь могла относиться к отказному заданию): связи и сведения об инициаторе с
    /// таким № не привязываются, а уходят в отчёт.
    /// </summary>
    /// <returns>№ задания, которые в базе повторяются (без регистра).</returns>
    private static HashSet<string> MarkDuplicateTasks(List<AccessTaskData> tasks)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => !t.IsEmpty))
        {
            if (!keys.Add(task.Key))
            {
                task.Problem ??= $"повтор кода записи {task.Key} в базе";
            }
        }

        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var group in tasks.Where(t => !t.IsEmpty && t.Number is not null)
            .GroupBy(t => t.Number!, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1))
        {
            ambiguous.Add(group.Key);
            var rows = group.ToList();
            var first = true;
            foreach (var task in rows.Where(t => t.Problem is null))
            {
                var others = string.Join(", ", rows.Where(o => !ReferenceEquals(o, task)).Select(o => o.RowLabel));
                if (first)
                {
                    first = false;
                    task.TaskHeadLines.Add($"№ задания «{task.Number}» в базе повторяется: также у {others}. Связи и сведения об инициаторе с этим № не привязаны автоматически.");
                    continue;
                }

                task.CaseNumber = task.KeyText is null
                    ? $"{task.Number} (строка {task.Ordinal.ToString(CultureInfo.InvariantCulture)})"
                    : $"{task.Number} (запись {task.KeyText})";
                if (task.CaseNumber.Length > CreateCaseValidator.MaxNumberLength)
                {
                    task.Problem = $"№ задания «{task.Number}» повторяется в базе, а № дела с пометкой записи длиннее {CreateCaseValidator.MaxNumberLength} знаков";
                    continue;
                }

                task.TaskHeadLines.Add($"№ задания «{task.Number}» в базе повторяется (также у {others}) — делу дан № «{task.CaseNumber}».");
                task.Warnings.Add($"№ задания «{task.Number}» повторяется в базе — делу дан № «{task.CaseNumber}»");
            }
        }

        return ambiguous;
    }

    /// <summary>Повтор кода связи — отказ второй строки: соответствие «строка → фигурант» по коду иначе спутало бы связи.</summary>
    private static void MarkDuplicateLinks(List<AccessLinkData> links)
    {
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in links.Where(l => !l.IsEmpty && l.KeyText is not null))
        {
            if (!keys.Add(link.Key))
            {
                link.Problem ??= $"повтор кода связи {link.Key} в базе";
            }
        }
    }

    private static AccessLinkData ParseLink(
        int ordinal, IReadOnlyDictionary<string, object?> row, AccessMapping mapping, Dictionary<string, int> seen)
    {
        string? Text(AccessField field) =>
            mapping.LinkColumns.TryGetValue(field, out var column) ? AccessValues.Text(row.GetValueOrDefault(column)) : null;

        // Без кода записи ключ — отпечаток содержимого: повтор переноса узнаёт ту же связь; одинаковые строки
        // различаются счётчиком, чтобы обе дошли до дела. Фото в отпечаток не входит (первым чтением не читается).
        var keyText = Text(AccessField.LinkKey);
        var key = keyText;
        if (key is null)
        {
            var content = string.Join('|', mapping.LinkColumns.Where(c => !c.Key.IsPhoto()).OrderBy(c => c.Key)
                .Select(c => AccessValues.Text(row.GetValueOrDefault(c.Value))));
            var hash = "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
            var count = seen[hash] = seen.GetValueOrDefault(hash) + 1;
            key = count == 1 ? hash : hash + "-" + count.ToString(CultureInfo.InvariantCulture);
        }

        // Пустая строка связи (в реальной базе отдела таких две из трёх) — не ошибка: число видно в проверке.
        var hasPhoto = mapping.LinkColumns.TryGetValue(AccessField.LinkPhoto, out var photoColumn)
            && row.GetValueOrDefault(photoColumn) is byte[] { Length: > 0 };
        if (!hasPhoto && IsEmptyRow(row, mapping.LinkColumns, AccessField.LinkKey))
        {
            return new AccessLinkData { Ordinal = ordinal, Key = key, KeyText = keyText, IsEmpty = true };
        }

        var notes = new List<string>();
        var warnings = new List<string>();
        var name = AccessValues.FitField(Text(AccessField.LinkName), 500, "ФИО связи", notes);
        var alias = AccessValues.FitField(Text(AccessField.LinkAlias), PersonQuestionnaireValidator.MaxAliasLength, "Кличка", notes);
        var linkType = Text(AccessField.LinkType);
        if (linkType is { Length: > 300 })
        {
            notes.Add("Связь объекта (полностью): " + linkType);
            linkType = AccessValues.Fit(linkType, 300);
        }

        // «Сведения об объекте связи» — в ориентировку анкеты связи (ADR-0055); не влезло — полностью в примечание.
        var orientation = AccessValues.FitField(
            Text(AccessField.LinkInfo), PersonQuestionnaireValidator.MaxOrientationLength, "Сведения об объекте связи", notes);

        // Автомобиль: госномер или марка обязательны; номер без букв и цифр или длиннее поля — в примечание.
        var plate = Text(AccessField.CarPlate);
        var carNotes = new List<string>();
        if (plate is not null && (plate.Length > Persons.SavePersonVehicleValidator.MaxPlateLength || !plate.Any(char.IsLetterOrDigit)))
        {
            carNotes.Add("Гос. номер в базе: " + plate);
            plate = null;
        }

        var make = AccessValues.FitField(Text(AccessField.CarMake), Persons.SavePersonVehicleValidator.MaxMakeLength, "Марка", carNotes);
        if (Text(AccessField.CarOwner) is { } owner)
        {
            carNotes.Insert(0, "Владелец: " + owner);
        }

        if (plate is null && make is null && carNotes.Count > 0)
        {
            // Автомобиля без номера и марки не завести — сведения о нём идут в примечание связи.
            notes.AddRange(carNotes);
            carNotes.Clear();
        }

        var addresses = (Text(AccessField.VisitAddresses) ?? string.Empty)
            .Split(['\r', '\n', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(a => a.Any(char.IsLetterOrDigit))
            .Select(a => AccessValues.Fit(a, Persons.SavePersonAddressValidator.MaxTextLength))
            .ToList();

        var link = new AccessLinkData
        {
            Ordinal = ordinal,
            Key = key,
            KeyText = keyText,
            TaskRef = Text(AccessField.LinkTaskNumber),
            LinkType = linkType,
            Name = name,
            Alias = alias,
            Orientation = orientation,
            Notes = AccessValues.JoinNotes(notes, 4000, "примечание связи", warnings),
            CarMake = make,
            CarPlate = plate,
            CarNotes = AccessValues.JoinNotes(carNotes, Persons.SavePersonVehicleValidator.MaxNotesLength, "примечание автомобиля", warnings),
            Warnings = warnings,
            Addresses = addresses,
            HasPhoto = hasPhoto,
        };

        var noDetails = link.Name is null && link.Alias is null && link.Orientation is null && link.LinkType is null && link.Notes is null
            && link.CarPlate is null && link.CarMake is null && link.Addresses.Count == 0;
        if (link.TaskRef is null)
        {
            link.Problem = noDetails && hasPhoto ? AccessImporter.OnlyPhotoLinkMessage : "не указан № задания — связь не к чему привязать";
        }
        else if (noDetails && !hasPhoto)
        {
            // Строка «№ задания + фото» — неустановленная связь, известная по снимку: переносится.
            link.Problem = "в записи связи только № задания — сведений о связи нет";
        }

        return link;
    }

    private static void AttachLinks(List<AccessTaskData> tasks, List<AccessLinkData> links, HashSet<string> ambiguous)
    {
        // Ссылка связи на задание — по № задания, а если такого номера в базе нет ни у одного задания — по коду записи
        // задания (в базах бывает и так, и так). Номер отказного задания — тоже номер: его связь идёт в отчёт, а не к
        // чужому заданию с таким же кодом записи. № задания, который в базе повторяется, не указывает на одно задание —
        // такая связь в отчёт, её привязывают вручную.
        var (byNumber, byKey) = Index(tasks);
        foreach (var link in links.Where(l => !l.IsEmpty && l.Problem is null))
        {
            if (ambiguous.Contains(link.TaskRef!))
            {
                link.Problem = $"№ задания «{link.TaskRef}» в базе у нескольких заданий — связь не привязана, привяжите её вручную";
                continue;
            }

            var task = byNumber.GetValueOrDefault(link.TaskRef!) ?? byKey.GetValueOrDefault(link.TaskRef!);
            if (task is null)
            {
                link.Problem = $"задание «{link.TaskRef}» в базе не найдено";
                continue;
            }

            if (task.Problem is not null)
            {
                link.Problem = $"задание «{link.TaskRef}» не переносится — связь тоже";
                continue;
            }

            link.Task = task;
            task.Links.Add(link);
        }
    }

    // Все непустые задания — и отказные: ссылка на отказное задание узнаётся и идёт в отчёт, а не мимо него.
    private static (Dictionary<string, AccessTaskData> ByNumber, Dictionary<string, AccessTaskData> ByKey) Index(List<AccessTaskData> tasks)
    {
        var byNumber = new Dictionary<string, AccessTaskData>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, AccessTaskData>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => !t.IsEmpty))
        {
            if (task.Number is not null)
            {
                byNumber.TryAdd(task.Number, task);
            }

            byKey.TryAdd(task.Key, task);
        }

        return (byNumber, byKey);
    }

    /// <summary>
    /// Сведения таблицы инициаторов (реальная база отдела: «инициатор задания» — управление, «нач управления»,
    /// «исполнитель», «отдел», «телефон» — по № задания) в реквизиты инициатора задания (решение заказчика 16.10.2026):
    /// управление — запись справочника ГУ (таблица точнее поля задания; расхождение — в примечание задания),
    /// исполнитель — ФИО инициатора, телефон — телефон, начальник управления и отдел — прочие реквизиты. Ничего не
    /// теряется молча: не влезшее в поле — в примечание, неприменённая строка — в отчёт.
    /// </summary>
    private static InitiatorRows AttachInitiators(
        List<AccessTaskData> tasks, List<IReadOnlyDictionary<string, object?>> rows, AccessMapping mapping, HashSet<string> ambiguous)
    {
        // Только по № задания — без запасного поиска по коду записи: № в таблице инициаторов, которого нет среди
        // заданий (опечатка, удалённое задание), не должен лечь в чужое дело с таким же кодом записи.
        var (byNumber, _) = Index(tasks);
        var problems = new List<(string, string)>();
        var applied = new HashSet<AccessTaskData>();
        var empty = 0;
        var ordinal = 0;
        foreach (var row in rows)
        {
            ordinal++;
            string? Text(AccessField field) =>
                mapping.InitiatorColumns.TryGetValue(field, out var column) ? AccessValues.Text(row.GetValueOrDefault(column)) : null;

            var number = Text(AccessField.InitiatorTaskNumber);
            var unit = Text(AccessField.InitiatorUnit);
            var head = Text(AccessField.InitiatorHead);
            var executor = Text(AccessField.InitiatorExecutor);
            var department = Text(AccessField.InitiatorDepartment);
            var phone = Text(AccessField.InitiatorPhone);
            if (unit is null && head is null && executor is null && department is null && phone is null)
            {
                // Строка без единого сведения (в реальной базе — только № задания) — переносить нечего.
                empty++;
                continue;
            }

            var key = number ?? "#" + ordinal.ToString(CultureInfo.InvariantCulture);
            if (number is null)
            {
                problems.Add((key, "не указан № задания — сведения об инициаторе не к чему привязать"));
                continue;
            }

            if (ambiguous.Contains(number))
            {
                problems.Add((key, $"№ задания «{number}» в базе у нескольких заданий — сведения об инициаторе не перенесены, внесите их вручную"));
                continue;
            }

            var task = byNumber.GetValueOrDefault(number);
            if (task is null)
            {
                problems.Add((key, $"задание «{number}» в базе не найдено — сведения об инициаторе не перенесены"));
                continue;
            }

            if (task.Problem is not null)
            {
                problems.Add((key, $"задание «{number}» не переносится — сведения об инициаторе тоже"));
                continue;
            }

            if (!applied.Add(task))
            {
                problems.Add((key, $"повтор № задания «{number}» в таблице инициаторов — взята первая строка"));
                continue;
            }

            if (unit is not null)
            {
                var fromTable = AccessValues.FitField(unit, MaxInitiatorLength, "Инициатор задания", task.TaskOverflowLines)!;
                if (task.Initiator is { } fromTask && AccessValues.ReferenceKey(fromTask) != AccessValues.ReferenceKey(fromTable))
                {
                    task.TaskHeadLines.Add($"Инициатор в задании: «{fromTask}»; в таблице инициаторов: «{fromTable}» — взят из таблицы.");
                }

                task.Initiator = fromTable;
            }

            task.InitiatorName = AccessValues.FitField(executor, TaskRequisitesValidator.MaxInitiatorNameLength, "Исполнитель у инициатора", task.TaskOverflowLines);
            task.InitiatorPhone = AccessValues.FitField(phone, TaskRequisitesValidator.MaxInitiatorPhoneLength, "Телефон инициатора", task.TaskOverflowLines);
            var details = string.Join("; ", new[]
            {
                head is null ? null : "Начальник управления: " + head,
                department is null ? null : "Отдел: " + department,
            }.Where(part => part is not null));
            task.InitiatorDetails = AccessValues.FitField(
                details.Length == 0 ? null : details, TaskRequisitesValidator.MaxInitiatorDetailsLength, "Сведения об инициаторе", task.TaskOverflowLines);
            task.HasInitiatorRow = true;
        }

        return new InitiatorRows(problems, empty);
    }

    /// <summary>Итог разбора таблицы инициаторов: неприменённые строки и число пустых.</summary>
    private sealed record InitiatorRows(IReadOnlyList<(string Key, string Message)> Problems, int Empty)
    {
        public static InitiatorRows None { get; } = new([], 0);
    }
}
