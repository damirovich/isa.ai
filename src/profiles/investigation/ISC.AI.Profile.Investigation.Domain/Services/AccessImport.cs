namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>
/// Поле системы, в которое переносится столбец базы Access (ADR-0043). Задания — личные и оперативные данные объекта;
/// связи — связь объекта с автомобилем, адресами посещения и фото; инициаторы — сведения об инициаторе задания (таблица
/// «инициатор» реальной базы отдела: управление, начальник, исполнитель, отдел, телефон — по № задания).
/// </summary>
public enum AccessField
{
    /// <summary>Код записи задания (ключ строки в базе).</summary>
    TaskKey = 1,

    /// <summary>№ задания.</summary>
    TaskNumber = 2,

    /// <summary>Дата задания.</summary>
    TaskDate = 3,

    /// <summary>Фамилия объекта.</summary>
    LastName = 4,

    /// <summary>Имя объекта.</summary>
    FirstName = 5,

    /// <summary>Отчество объекта.</summary>
    MiddleName = 6,

    /// <summary>ФИО одним полем (если в базе нет раздельных).</summary>
    FullName = 7,

    /// <summary>Год или дата рождения.</summary>
    BirthYear = 8,

    /// <summary>Пол.</summary>
    Sex = 9,

    /// <summary>Место рождения.</summary>
    BirthPlace = 10,

    /// <summary>Место работы.</summary>
    WorkPlace = 11,

    /// <summary>Место жительства.</summary>
    Residence = 12,

    /// <summary>Фото объекта (поле OLE).</summary>
    Photo = 13,

    /// <summary>Кличка.</summary>
    Alias = 14,

    /// <summary>Мероприятие.</summary>
    Measure = 15,

    /// <summary>Инициатор (ГУ).</summary>
    Initiator = 16,

    /// <summary>Обоснование.</summary>
    Justification = 17,

    /// <summary>Цель мероприятия.</summary>
    Purpose = 18,

    /// <summary>Сведения об объекте — в ориентировку анкеты (ADR-0055).</summary>
    ObjectInfo = 19,

    /// <summary>Результат мероприятия.</summary>
    Result = 20,

    /// <summary>Гражданство объекта — в поле гражданства анкеты (ADR-0055).</summary>
    Citizenship = 21,

    /// <summary>Код записи связи.</summary>
    LinkKey = 101,

    /// <summary>№ задания, к которому относится связь.</summary>
    LinkTaskNumber = 102,

    /// <summary>Связь объекта (вид связи: брат, знакомый…).</summary>
    LinkType = 103,

    /// <summary>Сведения об объекте связи — в ориентировку анкеты связи (ADR-0055).</summary>
    LinkInfo = 104,

    /// <summary>ФИО связи (если в базе есть).</summary>
    LinkName = 105,

    /// <summary>Марка автомобиля.</summary>
    CarMake = 106,

    /// <summary>Гос. номер.</summary>
    CarPlate = 107,

    /// <summary>Владелец автомобиля.</summary>
    CarOwner = 108,

    /// <summary>Адреса посещения связи.</summary>
    VisitAddresses = 109,

    /// <summary>Кличка объекта связи (в реальной базе ФИО связи нет — связь узнают по кличке).</summary>
    LinkAlias = 110,

    /// <summary>Фото объекта связи (поле OLE).</summary>
    LinkPhoto = 111,

    /// <summary>№ задания, к которому относятся сведения об инициаторе.</summary>
    InitiatorTaskNumber = 201,

    /// <summary>Инициатор задания — управление (ГУ), запись справочника.</summary>
    InitiatorUnit = 202,

    /// <summary>Начальник управления-инициатора (ФИО).</summary>
    InitiatorHead = 203,

    /// <summary>Исполнитель у инициатора (ФИО).</summary>
    InitiatorExecutor = 204,

    /// <summary>Отдел инициатора.</summary>
    InitiatorDepartment = 205,

    /// <summary>Телефон инициатора.</summary>
    InitiatorPhone = 206,
}

/// <summary>Перечни и подписи полей переноса из Access.</summary>
public static class AccessFields
{
    /// <summary>Поля таблицы заданий в порядке экрана.</summary>
    public static IReadOnlyList<AccessField> TaskFields { get; } =
    [
        AccessField.TaskKey, AccessField.TaskNumber, AccessField.TaskDate, AccessField.LastName, AccessField.FirstName,
        AccessField.MiddleName, AccessField.FullName, AccessField.BirthYear, AccessField.Sex, AccessField.BirthPlace,
        AccessField.WorkPlace, AccessField.Residence, AccessField.Citizenship, AccessField.Photo, AccessField.Alias,
        AccessField.Measure, AccessField.Initiator, AccessField.Justification, AccessField.Purpose, AccessField.ObjectInfo,
        AccessField.Result,
    ];

    /// <summary>Поля таблицы связей в порядке экрана.</summary>
    public static IReadOnlyList<AccessField> LinkFields { get; } =
    [
        AccessField.LinkKey, AccessField.LinkTaskNumber, AccessField.LinkType, AccessField.LinkInfo, AccessField.LinkName,
        AccessField.LinkAlias, AccessField.CarMake, AccessField.CarPlate, AccessField.CarOwner, AccessField.VisitAddresses,
        AccessField.LinkPhoto,
    ];

    /// <summary>Поля таблицы инициаторов в порядке экрана.</summary>
    public static IReadOnlyList<AccessField> InitiatorFields { get; } =
    [
        AccessField.InitiatorTaskNumber, AccessField.InitiatorUnit, AccessField.InitiatorHead, AccessField.InitiatorExecutor,
        AccessField.InitiatorDepartment, AccessField.InitiatorPhone,
    ];

    /// <summary>Поле фото: ему подходит только двоичный столбец (объект OLE), остальным — только не двоичные.</summary>
    public static bool IsPhoto(this AccessField field) => field is AccessField.Photo or AccessField.LinkPhoto;

    /// <summary>Подпись поля на экране.</summary>
    public static string Label(this AccessField field) => field switch
    {
        AccessField.TaskKey => "Код записи",
        AccessField.TaskNumber => "№ задания",
        AccessField.TaskDate => "Дата задания",
        AccessField.LastName => "Фамилия",
        AccessField.FirstName => "Имя",
        AccessField.MiddleName => "Отчество",
        AccessField.FullName => "ФИО одним полем",
        AccessField.BirthYear => "Год (дата) рождения",
        AccessField.Sex => "Пол",
        AccessField.BirthPlace => "Место рождения",
        AccessField.WorkPlace => "Место работы",
        AccessField.Residence => "Место жительства",
        AccessField.Photo => "Фото",
        AccessField.Alias => "Кличка",
        AccessField.Measure => "Мероприятие",
        AccessField.Initiator => "Инициатор",
        AccessField.Justification => "Обоснование",
        AccessField.Purpose => "Цель мероприятия",
        AccessField.ObjectInfo => "Сведения об объекте",
        AccessField.Result => "Результат мероприятия",
        AccessField.Citizenship => "Гражданство",
        AccessField.LinkKey => "Код связи",
        AccessField.LinkTaskNumber => "№ задания",
        AccessField.LinkType => "Связь объекта",
        AccessField.LinkInfo => "Сведения об объекте связи",
        AccessField.LinkName => "ФИО связи",
        AccessField.CarMake => "Марка авто",
        AccessField.CarPlate => "Гос. номер",
        AccessField.CarOwner => "Владелец авто",
        AccessField.VisitAddresses => "Адреса посещения",
        AccessField.LinkAlias => "Кличка объекта связи",
        AccessField.LinkPhoto => "Фото связи",
        AccessField.InitiatorTaskNumber => "№ задания",
        AccessField.InitiatorUnit => "Инициатор задания (ГУ)",
        AccessField.InitiatorHead => "Начальник управления",
        AccessField.InitiatorExecutor => "Исполнитель",
        AccessField.InitiatorDepartment => "Отдел",
        AccessField.InitiatorPhone => "Телефон",
        _ => field.ToString(),
    };
}

/// <summary>Столбец таблицы базы Access.</summary>
/// <param name="Name">Имя столбца.</param>
/// <param name="TypeName">Тип данных (для подсказки на экране).</param>
/// <param name="IsBinary">Двоичное поле (OLE, вложение) — кандидат в фото.</param>
public sealed record AccessColumn(string Name, string TypeName, bool IsBinary);

/// <summary>Таблица базы Access.</summary>
/// <param name="Name">Имя таблицы.</param>
/// <param name="Columns">Столбцы.</param>
public sealed record AccessTable(string Name, IReadOnlyList<AccessColumn> Columns);

/// <summary>Сопоставление таблиц и столбцов базы с полями системы (ADR-0043 п. 2).</summary>
/// <param name="TasksTable">Таблица заданий.</param>
/// <param name="TaskColumns">Столбцы таблицы заданий по полям системы.</param>
/// <param name="LinksTable">Таблица связей; <see langword="null"/> — связи не переносятся.</param>
/// <param name="LinkColumns">Столбцы таблицы связей по полям системы.</param>
/// <param name="InitiatorsTable">Таблица сведений об инициаторе (по № задания); <see langword="null"/> — не переносится.</param>
/// <param name="InitiatorColumns">Столбцы таблицы инициаторов по полям системы.</param>
public sealed record AccessMapping(
    string? TasksTable,
    IReadOnlyDictionary<AccessField, string> TaskColumns,
    string? LinksTable,
    IReadOnlyDictionary<AccessField, string> LinkColumns,
    string? InitiatorsTable,
    IReadOnlyDictionary<AccessField, string> InitiatorColumns)
{
    /// <summary>Пустое сопоставление.</summary>
    public static AccessMapping Empty { get; } = new(
        null, new Dictionary<AccessField, string>(), null, new Dictionary<AccessField, string>(), null, new Dictionary<AccessField, string>());
}

/// <summary>
/// ПОРТ: чтение базы Access только на чтение (ADR-0043). Реализация — драйвер ACE OLE DB в слое данных профиля.
/// </summary>
public interface IAccessDatabaseReader
{
    /// <summary>Почему чтение недоступно (нет драйвера, не Windows); <see langword="null"/> — доступно.</summary>
    string? UnavailableReason { get; }

    /// <summary>
    /// Открывает базу из потока. Драйверу нужен файл, поэтому содержимое кладётся во временную копию, которая
    /// удаляется закрытием базы (<see cref="IAsyncDisposable.DisposeAsync"/>). Поток читается до конца.
    /// </summary>
    /// <param name="content">Содержимое файла базы.</param>
    /// <param name="extension">Расширение исходного файла (<c>.accdb</c> или <c>.mdb</c>).</param>
    /// <param name="cancellationToken">Отмена.</param>
    /// <exception cref="AccessDatabaseReadException">Не база Access, повреждена, с паролем или нет драйвера.</exception>
    Task<IAccessDatabase> OpenAsync(Stream content, string extension, CancellationToken cancellationToken = default);
}

/// <summary>Открытая на чтение база Access (временная копия удаляется при закрытии).</summary>
public interface IAccessDatabase : IAsyncDisposable
{
    /// <summary>Таблицы пользователя (без системных) со столбцами.</summary>
    IReadOnlyList<AccessTable> Tables { get; }

    /// <summary>
    /// Строки таблицы по одной (поток, а не список: поле фото у тысяч записей в память целиком не берётся):
    /// значения указанных столбцов, <see langword="null"/> вместо пустых.
    /// </summary>
    /// <param name="table">Таблица.</param>
    /// <param name="columns">Столбцы — только из схемы этой таблицы.</param>
    /// <param name="orderBy">Столбец порядка строк (код записи) или <see langword="null"/>.</param>
    /// <param name="cancellationToken">Отмена.</param>
    IAsyncEnumerable<IReadOnlyDictionary<string, object?>> ReadRowsAsync(
        string table, IReadOnlyCollection<string> columns, string? orderBy, CancellationToken cancellationToken = default);
}

/// <summary>Перенесённая запись базы Access: что из неё создано (ADR-0043 п. 6).</summary>
/// <param name="FileSha256">SHA-256 файла базы.</param>
/// <param name="SourceTable">Таблица базы.</param>
/// <param name="SourceKey">Код записи (или № задания, если кода нет).</param>
/// <param name="CaseId">Дело.</param>
/// <param name="PersonId">Фигурант (объект или связь); <see langword="null"/> — ещё не создан.</param>
public sealed record AccessImportRecord(string FileSha256, string SourceTable, string SourceKey, int CaseId, int? PersonId);

/// <summary>
/// ПОРТ: учёт перенесённых записей баз Access (<c>investigation.access_import_record</c>) — повтор переноса той же базы
/// без дублей.
/// </summary>
public interface IAccessImportRecordStore
{
    /// <summary>Перенесённые записи файла по (таблица, код).</summary>
    Task<IReadOnlyDictionary<(string Table, string Key), AccessImportRecord>> ListAsync(
        string fileSha256, CancellationToken cancellationToken = default);

    /// <summary>Записывает или обновляет соответствие записи (фигурант дописывается после создания).</summary>
    Task SaveAsync(AccessImportRecord record, CancellationToken cancellationToken = default);
}

/// <summary>
/// Базу Access не удалось прочитать: файл не база Access, повреждён, защищён паролем или нет драйвера.
/// Сообщение — для человека, уже без технических подробностей драйвера.
/// </summary>
public sealed class AccessDatabaseReadException : Exception
{
    /// <summary>Создаёт исключение без сообщения (для анализаторов).</summary>
    public AccessDatabaseReadException()
    {
    }

    /// <summary>Создаёт исключение с сообщением для человека.</summary>
    public AccessDatabaseReadException(string message)
        : base(message)
    {
    }

    /// <summary>Создаёт исключение с сообщением для человека и исходной ошибкой драйвера.</summary>
    public AccessDatabaseReadException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
