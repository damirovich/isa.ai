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

    /// <summary>Нормализованный ключ записи справочника: регистр, «ё», пробелы не различаются.</summary>
    public static string ReferenceKey(string name) =>
        string.Join(' ', name.ToLowerInvariant().Replace('ё', 'е').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

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

    public string? Number { get; init; }

    public required string CaseNumber { get; init; }

    public DateOnly? Date { get; init; }

    public string? DisplayName { get; init; }

    public PersonQuestionnaire Questionnaire { get; init; } = PersonQuestionnaire.Empty;

    public string? PersonNotes { get; init; }

    public string? Initiator { get; init; }

    public required string Justification { get; init; }

    public required string Purpose { get; init; }

    public string? TaskNotes { get; init; }

    public List<AccessLinkData> Links { get; } = [];

    /// <summary>Что перенесётся не полностью (обрезка примечаний) — в отчёт, запись при этом переносится.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    /// <summary>Почему запись не переносится; <see langword="null"/> — переносится.</summary>
    public string? Problem { get; set; }
}

/// <summary>Связь из базы, разобранная в значения системы.</summary>
internal sealed class AccessLinkData
{
    public required int Ordinal { get; init; }

    public required string Key { get; init; }

    public string? TaskRef { get; init; }

    public string? LinkType { get; init; }

    public string? Name { get; init; }

    public string? Notes { get; init; }

    public string? CarMake { get; init; }

    public string? CarPlate { get; init; }

    public string? CarNotes { get; init; }

    public IReadOnlyList<string> Addresses { get; init; } = [];

    /// <summary>Что перенесётся не полностью (обрезка примечаний) — в отчёт, запись при этом переносится.</summary>
    public IReadOnlyList<string> Warnings { get; init; } = [];

    public AccessTaskData? Task { get; set; }

    public string? Problem { get; set; }
}

/// <summary>
/// Содержимое базы, разобранное по сопоставлению (без фото): задания, связи и их привязка к заданиям. Одинаково
/// строится проверкой и переносом — что показала проверка, то и переносится.
/// </summary>
internal sealed class AccessSnapshot
{
    /// <summary>Заменитель пустого обязательного реквизита задания (ADR-0043 п. 5).</summary>
    public const string NotSpecified = "не указано в исходной базе";

    private AccessSnapshot(IReadOnlyList<AccessTaskData> tasks, IReadOnlyList<AccessLinkData> links)
    {
        Tasks = tasks;
        Links = links;
    }

    /// <summary>Задания в порядке чтения.</summary>
    public IReadOnlyList<AccessTaskData> Tasks { get; }

    /// <summary>Связи в порядке чтения.</summary>
    public IReadOnlyList<AccessLinkData> Links { get; }

    /// <summary>Порядок строк заданий: по коду записи, если он сопоставлен (проверка и перенос читают одинаково).</summary>
    public static string? TaskOrder(AccessMapping mapping) => mapping.TaskColumns.GetValueOrDefault(AccessField.TaskKey);

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

        if (mapping.LinksTable is null)
        {
            return null;
        }

        if (string.Equals(mapping.LinksTable, mapping.TasksTable, StringComparison.Ordinal))
        {
            return "Таблица связей должна быть другой, не таблицей заданий.";
        }

        error = ValidateTable(tables, mapping.LinksTable, mapping.LinkColumns, AccessFields.LinkFields);
        if (error is not null)
        {
            return error;
        }

        return mapping.LinkColumns.ContainsKey(AccessField.LinkTaskNumber)
            ? null
            : "Укажите в таблице связей столбец № задания — без него связь не к чему привязать.";
    }

    /// <summary>Отпечаток «файл + сопоставление + выбор оператора»: запуск сверяет его с проверкой.</summary>
    public static string Fingerprint(string sha256, AccessMapping mapping, AccessImportOptions options)
    {
        var text = new StringBuilder()
            .Append(sha256).Append('|').Append(mapping.TasksTable).Append('|').Append(mapping.LinksTable).Append('|')
            .AppendJoin(';', mapping.TaskColumns.OrderBy(c => c.Key).Select(c => $"{(int)c.Key}={c.Value}")).Append('|')
            .AppendJoin(';', mapping.LinkColumns.OrderBy(c => c.Key).Select(c => $"{(int)c.Key}={c.Value}")).Append('|')
            .Append(CultureInfo.InvariantCulture, $"{options.DivisionId}|{options.Classification}|{(int)options.Status}|{options.CreateMissingReferences}")
            .ToString();
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
    }

    /// <summary>Читает и разбирает задания и связи.</summary>
    /// <param name="database">Открытая база.</param>
    /// <param name="mapping">Сопоставление (уже проверенное <see cref="Validate"/>).</param>
    /// <param name="fileName">Имя файла базы — для примечания «откуда перенесено».</param>
    /// <param name="today">Сегодня (дата задания без даты в базе, правдоподобие года рождения).</param>
    /// <param name="cancellationToken">Отмена.</param>
    public static async Task<AccessSnapshot> LoadAsync(
        IAccessDatabase database, AccessMapping mapping, string fileName, DateOnly today, CancellationToken cancellationToken)
    {
        var tasks = new List<AccessTaskData>();
        var taskColumns = mapping.TaskColumns.Where(c => c.Key != AccessField.Photo).Select(c => c.Value).ToList();
        var ordinal = 0;
        await foreach (var row in database.ReadRowsAsync(mapping.TasksTable!, taskColumns, TaskOrder(mapping), cancellationToken))
        {
            tasks.Add(ParseTask(++ordinal, row, mapping, fileName, today));
        }

        MarkDuplicateTasks(tasks);

        var links = new List<AccessLinkData>();
        if (mapping.LinksTable is not null)
        {
            var linkColumns = mapping.LinkColumns.Select(c => c.Value).ToList();
            var seen = new Dictionary<string, int>(StringComparer.Ordinal);
            ordinal = 0;
            await foreach (var row in database.ReadRowsAsync(
                mapping.LinksTable, linkColumns, mapping.LinkColumns.GetValueOrDefault(AccessField.LinkKey), cancellationToken))
            {
                links.Add(ParseLink(++ordinal, row, mapping, seen));
            }

            AttachLinks(tasks, links);
        }

        return new AccessSnapshot(tasks, links);
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

    private static AccessTaskData ParseTask(
        int ordinal, IReadOnlyDictionary<string, object?> row, AccessMapping mapping, string fileName, DateOnly today)
    {
        object? Value(AccessField field) =>
            mapping.TaskColumns.TryGetValue(field, out var column) ? row.GetValueOrDefault(column) : null;
        string? Text(AccessField field) => AccessValues.Text(Value(field));

        var number = Text(AccessField.TaskNumber);
        var keyText = Text(AccessField.TaskKey);
        var key = keyText ?? number ?? "#" + ordinal.ToString(CultureInfo.InvariantCulture);
        var personNotes = new List<string>();
        var taskNotes = new List<string>();
        var warnings = new List<string>();
        string? problem = null;

        // Объект: ФИО одним полем или из частей; без имени запись не переносится — это не задание, а пустая строка.
        var fullName = Text(AccessField.FullName)
            ?? string.Join(' ', new[] { Text(AccessField.LastName), Text(AccessField.FirstName), Text(AccessField.MiddleName) }
                .Where(part => part is not null));
        var displayName = AccessValues.FitField(string.IsNullOrWhiteSpace(fullName) ? null : fullName, 500, "ФИО", personNotes);
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

        var questionnaire = new PersonQuestionnaire(
            birthDate,
            birthYear,
            AccessValues.FitField(Text(AccessField.BirthPlace), PersonQuestionnaireValidator.MaxPlaceLength, "Место рождения", personNotes),
            AccessValues.FitField(Text(AccessField.WorkPlace), PersonQuestionnaireValidator.MaxPlaceLength, "Место работы", personNotes),
            AccessValues.FitField(Text(AccessField.Residence), PersonQuestionnaireValidator.MaxResidenceLength, "Место жительства", personNotes),
            sex,
            AccessValues.FitField(Text(AccessField.Alias), PersonQuestionnaireValidator.MaxAliasLength, "Кличка", personNotes));
        if (Text(AccessField.ObjectInfo) is { } objectInfo)
        {
            personNotes.Insert(0, "Сведения об объекте: " + objectInfo);
        }

        // Задание: пустые обязательные реквизиты — пометкой, а не выдумкой (ADR-0043 п. 5).
        var dateValue = Value(AccessField.TaskDate);
        var date = AccessValues.Date(dateValue);
        if (date is null && AccessValues.Text(dateValue) is { } dateText)
        {
            taskNotes.Add($"Дата задания в базе: «{dateText}» — не распознана, дело открыто датой переноса");
        }
        else if (date is null && mapping.TaskColumns.ContainsKey(AccessField.TaskDate))
        {
            taskNotes.Add("Дата задания в базе не указана — дело открыто датой переноса");
        }

        var justification = AccessValues.FitField(Text(AccessField.Justification), TaskRequisitesValidator.MaxJustificationLength, "Обоснование", taskNotes);
        var purpose = AccessValues.FitField(Text(AccessField.Purpose), TaskRequisitesValidator.MaxPurposeLength, "Цель мероприятия", taskNotes);
        if (Text(AccessField.Measure) is { } measure)
        {
            taskNotes.Insert(0, "Мероприятие: " + measure);
        }

        if (Text(AccessField.Result) is { } result)
        {
            taskNotes.Add("Результат мероприятия: " + result);
        }

        taskNotes.Add($"Перенесено из базы Access «{fileName}», таблица «{mapping.TasksTable}», код записи {key}.");

        return new AccessTaskData
        {
            Ordinal = ordinal,
            Key = key,
            KeyText = keyText,
            Number = number,
            CaseNumber = caseNumber,
            Date = date,
            DisplayName = displayName,
            Questionnaire = questionnaire,
            PersonNotes = AccessValues.JoinNotes(personNotes, 4000, "примечание объекта", warnings),
            Initiator = Text(AccessField.Initiator) is { } initiator ? AccessValues.Fit(initiator, 300) : null,
            Justification = justification ?? NotSpecified,
            Purpose = purpose ?? NotSpecified,
            TaskNotes = AccessValues.JoinNotes(taskNotes, TaskRequisitesValidator.MaxNotesLength, "примечание задания", warnings),
            Warnings = warnings,
            Problem = problem,
        };
    }

    private static void MarkDuplicateTasks(List<AccessTaskData> tasks)
    {
        // Повтор кода или № задания в самой базе — отказ второй записи: сливать два задания в одно дело нельзя.
        var keys = new Dictionary<string, AccessTaskData>(StringComparer.Ordinal);
        var numbers = new Dictionary<string, AccessTaskData>(StringComparer.OrdinalIgnoreCase);
        foreach (var task in tasks)
        {
            if (!keys.TryAdd(task.Key, task))
            {
                task.Problem ??= $"повтор кода записи {task.Key} в базе";
            }
            else if (task.Problem is null && !numbers.TryAdd(task.CaseNumber, task))
            {
                task.Problem = $"повтор № задания «{task.CaseNumber}» в базе (уже у записи {numbers[task.CaseNumber].Key})";
            }
        }
    }

    private static AccessLinkData ParseLink(
        int ordinal, IReadOnlyDictionary<string, object?> row, AccessMapping mapping, Dictionary<string, int> seen)
    {
        string? Text(AccessField field) =>
            mapping.LinkColumns.TryGetValue(field, out var column) ? AccessValues.Text(row.GetValueOrDefault(column)) : null;

        var notes = new List<string>();
        var warnings = new List<string>();
        var name = AccessValues.FitField(Text(AccessField.LinkName), 500, "ФИО связи", notes);
        var linkType = Text(AccessField.LinkType);
        if (linkType is { Length: > 300 })
        {
            notes.Add("Связь объекта (полностью): " + linkType);
            linkType = AccessValues.Fit(linkType, 300);
        }

        if (Text(AccessField.LinkInfo) is { } info)
        {
            notes.Insert(0, "Сведения об объекте связи: " + info);
        }

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

        // Без кода записи ключ — отпечаток содержимого: повтор переноса узнаёт ту же связь; одинаковые строки
        // различаются счётчиком, чтобы обе дошли до дела.
        var key = Text(AccessField.LinkKey);
        if (key is null)
        {
            var content = string.Join('|', mapping.LinkColumns.OrderBy(c => c.Key).Select(c => AccessValues.Text(row.GetValueOrDefault(c.Value))));
            var hash = "h" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content)))[..16];
            var count = seen[hash] = seen.GetValueOrDefault(hash) + 1;
            key = count == 1 ? hash : hash + "-" + count.ToString(CultureInfo.InvariantCulture);
        }

        var link = new AccessLinkData
        {
            Ordinal = ordinal,
            Key = key,
            TaskRef = Text(AccessField.LinkTaskNumber),
            LinkType = linkType,
            Name = name,
            Notes = AccessValues.JoinNotes(notes, 4000, "примечание связи", warnings),
            CarMake = make,
            CarPlate = plate,
            CarNotes = AccessValues.JoinNotes(carNotes, Persons.SavePersonVehicleValidator.MaxNotesLength, "примечание автомобиля", warnings),
            Warnings = warnings,
            Addresses = addresses,
        };

        if (link.TaskRef is null)
        {
            link.Problem = "не указан № задания — связь не к чему привязать";
        }
        else if (link.Name is null && link.LinkType is null && link.Notes is null && link.CarPlate is null
            && link.CarMake is null && link.Addresses.Count == 0)
        {
            link.Problem = "пустая запись связи";
        }

        return link;
    }

    private static void AttachLinks(List<AccessTaskData> tasks, List<AccessLinkData> links)
    {
        // Ссылка связи на задание — по № задания, а если такого номера нет — по коду записи задания (в базах бывает и
        // так, и так). Отказные задания в поиске не участвуют: их связи — тоже в отчёт.
        var byNumber = new Dictionary<string, AccessTaskData>(StringComparer.OrdinalIgnoreCase);
        var byKey = new Dictionary<string, AccessTaskData>(StringComparer.Ordinal);
        foreach (var task in tasks.Where(t => t.Problem is null))
        {
            if (task.Number is not null)
            {
                byNumber.TryAdd(task.Number, task);
            }

            byKey.TryAdd(task.Key, task);
        }

        foreach (var link in links.Where(l => l.Problem is null))
        {
            var task = byNumber.GetValueOrDefault(link.TaskRef!) ?? byKey.GetValueOrDefault(link.TaskRef!);
            if (task is null)
            {
                link.Problem = $"задание «{link.TaskRef}» в базе не найдено или не переносится";
                continue;
            }

            link.Task = task;
            task.Links.Add(link);
        }
    }
}
