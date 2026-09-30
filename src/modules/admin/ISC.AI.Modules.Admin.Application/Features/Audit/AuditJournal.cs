using System.Globalization;
using System.Text;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;

namespace ISC.AI.Modules.Admin.Application.Features.Audit;

/// <summary>Строка журнала для экрана: сама запись и то, что нужно человеку, чтобы её прочитать.</summary>
/// <param name="Record">Запись журнала (как её выдал порт — в пределах допуска смотрящего, ТБ-032).</param>
/// <param name="What">Что произошло — человекочитаемо (<see cref="AuditText.Describe"/>).</param>
/// <param name="SubjectLogin">Имя входа субъекта.</param>
/// <param name="DivisionName">Наименование подразделения записи.</param>
public sealed record AuditJournalRow(AuditRecordRow Record, string What, string? SubjectLogin, string? DivisionName);

/// <summary>Страница журнала для экрана.</summary>
/// <param name="Rows">Строки.</param>
/// <param name="TotalCount">Всего подходящих записей.</param>
public sealed record AuditJournalPage(IReadOnlyList<AuditJournalRow> Rows, int TotalCount);

/// <summary>Сотрудник для отбора «Кто» в журнале.</summary>
/// <param name="UserId">Идентификатор.</param>
/// <param name="Name">ФИО (или имя входа).</param>
/// <param name="UserName">Имя входа.</param>
/// <param name="IsActive">Учётная запись действует.</param>
public sealed record AuditSubject(int UserId, string Name, string UserName, bool IsActive);

/// <summary>Файл выгрузки журнала.</summary>
/// <param name="Content">Байты файла.</param>
/// <param name="FileName">Имя файла.</param>
/// <param name="RowCount">Сколько записей в файле.</param>
/// <param name="Truncated">Выгружены не все подходящие записи — упёрлись в потолок <see cref="AuditJournal.MaxExportRows"/>.</param>
public sealed record AuditExportFile(byte[] Content, string FileName, int RowCount, bool Truncated);

/// <summary>Общее для экрана и выгрузки журнала: имена сотрудников и подразделений, строка CSV.</summary>
public static class AuditJournal
{
    /// <summary>Потолок выгрузки: журнал большой, файл «за всё время» браузер и Excel не переварят.</summary>
    public const int MaxExportRows = 10_000;

    /// <summary>Имена сотрудников и подразделений для подписи строк журнала.</summary>
    public static async Task<AuditNames> LoadNamesAsync(
        IUserAccountStore accounts, IDivisionCatalog divisions, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(accounts);
        ArgumentNullException.ThrowIfNull(divisions);

        var logins = (await accounts.ListAsync(cancellationToken)).ToDictionary(a => a.UserId, a => a.UserName);
        var divisionNames = (await divisions.ListAsync(cancellationToken)).ToDictionary(d => d.Id, d => d.Name);
        return new AuditNames(logins, divisionNames);
    }

    /// <summary>Заголовок CSV.</summary>
    public const string CsvHeader = "№ записи;Время;Кто;Имя входа;Действие;Что произошло;Сводка;Объект;Гриф;Подразделение";

    /// <summary>Строка CSV (разделитель «;» — так Excel с русской локалью открывает файл по столбцам).</summary>
    public static string ToCsvLine(AuditJournalRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var r = row.Record;
        string[] cells =
        [
            r.Id.ToString(CultureInfo.InvariantCulture),
            r.OccurredAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture),
            r.SubjectName ?? (r.SubjectId is { } id ? $"пользователь № {id.ToString(CultureInfo.InvariantCulture)}" : string.Empty),
            row.SubjectLogin ?? string.Empty,
            AuditText.Label(r.Action),
            row.What,
            r.PayloadSensitive ?? string.Empty,
            r.ObjectRef ?? string.Empty,
            ClassificationLevels.Label(r.Classification),
            row.DivisionName ?? string.Empty,
        ];
        return string.Join(';', cells.Select(Escape));
    }

    // ТБ-012: ячейка, начинающаяся с «=», «+», «-», «@», Excel исполнил бы как формулу (CSV-инъекция: сводка
    // содержит текст, введённый пользователем). Такие ячейки получают ведущий апостроф; кавычки и разделители —
    // по правилам CSV.
    private static string Escape(string value)
    {
        if (value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r')
        {
            value = "'" + value;
        }

        return value.Contains(';') || value.Contains('"') || value.Contains('\n') || value.Contains('\r')
            ? "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\""
            : value;
    }

    /// <summary>Собрать файл CSV (UTF-8 с меткой — иначе Excel покажет кириллицу «кракозябрами»).</summary>
    public static byte[] ToCsv(IEnumerable<AuditJournalRow> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        var text = new StringBuilder().AppendLine(CsvHeader);
        foreach (var row in rows)
        {
            text.AppendLine(ToCsvLine(row));
        }

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }
}

/// <summary>Имена для подписи строк журнала.</summary>
/// <param name="Logins">Имя входа по идентификатору пользователя.</param>
/// <param name="Divisions">Наименование по идентификатору подразделения.</param>
public sealed record AuditNames(IReadOnlyDictionary<int, string> Logins, IReadOnlyDictionary<int, string> Divisions)
{
    /// <summary>Строка экрана из записи журнала.</summary>
    public AuditJournalRow ToJournalRow(AuditRecordRow record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return new AuditJournalRow(
            record,
            AuditText.Describe(record.Action, record.PayloadSensitive, record.ObjectRef),
            record.SubjectId is { } id && Logins.TryGetValue(id, out var login) ? login : null,
            record.DivisionId is { } d
                ? Divisions.TryGetValue(d, out var name) ? name : $"№ {d.ToString(CultureInfo.InvariantCulture)}"
                : null);
    }
}
