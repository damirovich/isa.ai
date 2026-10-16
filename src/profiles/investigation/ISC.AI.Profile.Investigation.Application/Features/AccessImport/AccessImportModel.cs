using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Application.Features.AccessImport;

/// <summary>
/// Что оператор выбирает для всей базы (ADR-0043 п. 4–5): в базе Access грифа нет, а подразделение — то, чей учёт
/// переносится. Подтверждаются галочкой перед запуском (ТБ-024).
/// </summary>
/// <param name="DivisionId">Подразделение всех заводимых дел.</param>
/// <param name="Classification">Гриф всех заводимых дел.</param>
/// <param name="Status">
/// Статус заданий: «в производстве» или «приостановлено». Закрывается перенесённое задание обычной сменой статуса в карточке —
/// тогда исполняется регламент закрытия (ТБ-074), которого при переносе ещё нет смысла исполнять: фото не обработаны.
/// </param>
/// <param name="CreateMissingReferences">Заводить недостающие записи справочников ГУ и типов связей.</param>
public sealed record AccessImportOptions(int DivisionId, short Classification, CaseStatus Status, bool CreateMissingReferences);

/// <summary>Загруженный файл базы: где лежит в хранилище и чем подтверждается неизменность (ТБ-077).</summary>
/// <param name="StoredFileName">Имя в хранилище (категория <see cref="AccessImportFiles.Category"/>, подкаталог — оператор).</param>
/// <param name="FileName">Исходное имя файла.</param>
/// <param name="Length">Размер, байт.</param>
/// <param name="Sha256">SHA-256 содержимого (hex, верхний регистр).</param>
public sealed record AccessDatabaseFile(string StoredFileName, string FileName, long Length, string Sha256);

/// <summary>Запись базы, которая не переносится или переносится не полностью, — с причиной.</summary>
/// <param name="Table">Таблица базы.</param>
/// <param name="Key">Код записи (или № задания).</param>
/// <param name="Message">Причина для человека.</param>
/// <param name="Warning">
/// Предупреждение: запись переносится, но не так, как в базе (№ дела с пометкой, обрезанное примечание, фото не
/// принято); <see langword="false"/> — ошибка, запись не переносится.
/// </param>
public sealed record AccessImportProblem(string Table, string Key, string Message, bool Warning = false);

/// <summary>Образец задания для проверки сопоставления глазами.</summary>
/// <param name="Key">Код записи.</param>
/// <param name="CaseNumber">№ дела (он же № задания).</param>
/// <param name="ObjectName">ФИО объекта.</param>
/// <param name="Date">Дата задания.</param>
/// <param name="Links">Связей у задания.</param>
/// <param name="Initiator">Инициатор (ГУ) как в базе.</param>
public sealed record AccessTaskPreview(string Key, string CaseNumber, string ObjectName, DateOnly? Date, int Links, string? Initiator);

/// <summary>Итог проверки до переноса — без записи (ADR-0043 п. 7).</summary>
/// <param name="TaskCount">Заданий в базе.</param>
/// <param name="NewTaskCount">Из них будет перенесено (не перенесены раньше и без ошибок).</param>
/// <param name="AlreadyImportedTasks">Из них перенесены раньше (повтор той же базы их пропустит).</param>
/// <param name="LinkCount">Связей в базе.</param>
/// <param name="NewLinkCount">Из них будет перенесено.</param>
/// <param name="PhotoCount">Заданий с распознанным фото (среди переносимых).</param>
/// <param name="InitiatorsToCreate">Записи справочника ГУ, которых нет и которые будут заведены.</param>
/// <param name="LinkTypesToCreate">Типы связей, которых нет и которые будут заведены.</param>
/// <param name="Problems">Записи с ошибками.</param>
/// <param name="Sample">Первые задания для сверки сопоставления.</param>
/// <param name="Fingerprint">Отпечаток «файл + сопоставление + выбор оператора» — запуск сверяет его с проверкой.</param>
/// <param name="EmptyRows">
/// Пустых строк (все сопоставленные поля пусты — так в Access остаются строки, начатые в форме и брошенные): не
/// переносятся и ошибками не считаются, но число видно.
/// </param>
/// <param name="LinkPhotoCount">Фото связей к приёму: у переносимых связей и у перенесённых раньше без принятого фото.</param>
/// <param name="InitiatorsApplied">Переносимых заданий, к которым найдены сведения в таблице инициаторов.</param>
public sealed record AccessImportPlan(
    int TaskCount,
    int NewTaskCount,
    int AlreadyImportedTasks,
    int LinkCount,
    int NewLinkCount,
    int PhotoCount,
    IReadOnlyList<string> InitiatorsToCreate,
    IReadOnlyList<string> LinkTypesToCreate,
    IReadOnlyList<AccessImportProblem> Problems,
    IReadOnlyList<AccessTaskPreview> Sample,
    string Fingerprint,
    int EmptyRows = 0,
    int LinkPhotoCount = 0,
    int InitiatorsApplied = 0)
{
    /// <summary>Есть что переносить (и фото связей, не принятые прошлой попыткой).</summary>
    public bool HasWork => NewTaskCount > 0 || NewLinkCount > 0 || LinkPhotoCount > 0;

    /// <summary>Записей с ошибками — они не переносятся.</summary>
    public int ErrorCount => Problems.Count(p => !p.Warning);

    /// <summary>Записей с предупреждениями — переносятся с пометкой.</summary>
    public int WarningCount => Problems.Count(p => p.Warning);
}

/// <summary>Итог переноса.</summary>
/// <param name="CasesCreated">Заведено дел (заданий).</param>
/// <param name="ObjectsCreated">Заведено объектов.</param>
/// <param name="LinksCreated">Заведено связей.</param>
/// <param name="PhotosAccepted">Фото принято в материалы дел.</param>
/// <param name="VehiclesSaved">Записано автомобилей.</param>
/// <param name="AddressesSaved">Записано адресов посещения.</param>
/// <param name="ReferencesCreated">Заведено записей справочников.</param>
/// <param name="AlreadyImported">Заданий пропущено как перенесённые раньше.</param>
/// <param name="Problems">Записи с ошибками.</param>
/// <param name="Stopped">Перенос прерван целиком (причина — первая в <paramref name="Problems"/>).</param>
/// <param name="LinkPhotosAccepted">Новых фото связей в материалах дел (повтор той же базы их не дублирует и не считает).</param>
/// <param name="EmptyRows">Пустых строк пропущено.</param>
public sealed record AccessImportReport(
    int CasesCreated,
    int ObjectsCreated,
    int LinksCreated,
    int PhotosAccepted,
    int VehiclesSaved,
    int AddressesSaved,
    int ReferencesCreated,
    int AlreadyImported,
    IReadOnlyList<AccessImportProblem> Problems,
    bool Stopped = false,
    int LinkPhotosAccepted = 0,
    int EmptyRows = 0);
