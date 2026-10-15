using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>
/// Файл реестра дел для Excel (ТФ-ДЕЛ-01): CSV с «;» и BOM — так его без вопросов открывает русский Excel. Первой и
/// последней строкой — гриф файла (наибольший из грифов выгруженных дел, ТБ-024), внизу — кто, когда и сколько дел
/// выгрузил. Значение, которое Excel принял бы за формулу («=», «+», «-», «@»), записывается как текст; номер дела и
/// номер задания — строкой-константой <c>="12/24"</c>, иначе Excel превратил бы «12/24» в дату.
/// </summary>
public static class CasesCsv
{
    private static readonly string[] Header =
    [
        "Номер", "Задание №", "Отдел", "Название", "Вид", "Статус", "Открыто", "Ведёт", "Инициатор (ГУ)",
        "Подразделение", "Гриф", "Носителей", "Фигурантов",
    ];

    /// <summary>Подписи названий для строки дела: подразделение, инициатор, кто ведёт.</summary>
    /// <param name="Division">Имя подразделения по идентификатору.</param>
    /// <param name="Initiator">Подразделение-инициатор задания по идентификатору справочника.</param>
    /// <param name="Investigator">Кто ведёт по идентификатору сотрудника.</param>
    public sealed record Names(Func<int, string> Division, Func<int?, string> Initiator, Func<int?, string> Investigator);

    /// <summary>Собрать файл (байты UTF-8 с BOM).</summary>
    /// <param name="rows">Дела в порядке реестра.</param>
    /// <param name="total">Сколько дел подходит под отбор (больше строк — выгрузка урезана пределом).</param>
    /// <param name="names">Подписи названий.</param>
    /// <param name="author">Кто сформировал.</param>
    /// <param name="at">Когда сформировано (местное время).</param>
    public static byte[] Build(IReadOnlyList<CaseRow> rows, int total, Names names, string author, DateTime at)
    {
        ArgumentNullException.ThrowIfNull(rows);
        ArgumentNullException.ThrowIfNull(names);

        var classification = rows.Count == 0 ? ClassificationLevels.Unclassified : rows.Max(r => ClassificationLevels.Normalize(r.Classification));
        var marking = Escape(ClassificationLevels.Marking(classification));
        var text = new StringBuilder();
        Line(text, marking);
        Line(text, Header.Select(Escape).ToArray());
        foreach (var row in rows)
        {
            Line(text,
                TextConstant(row.Number),
                row.TaskNumber is { } task ? TextConstant(task) : string.Empty,
                Escape(row.Direction?.ShortLabel()),
                Escape(row.Title),
                Escape(row.Kind.Label()),
                Escape(row.Status.Label()),
                row.OpenedAt.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture),
                Escape(names.Investigator(row.InvestigatorUserId)),
                Escape(names.Initiator(row.InitiatorUnitId)),
                Escape(names.Division(row.DivisionId)),
                Escape(ClassificationLevels.Label(row.Classification)),
                row.MediaCount.ToString(CultureInfo.InvariantCulture),
                row.PersonCount.ToString(CultureInfo.InvariantCulture));
        }

        Line(text);
        var count = total > rows.Count
            ? string.Create(CultureInfo.InvariantCulture, $"выгружено {rows.Count} из {total} (предел выгрузки)")
            : string.Create(CultureInfo.InvariantCulture, $"дел: {rows.Count}");
        Line(text, Escape(string.Create(CultureInfo.InvariantCulture, $"Сформировал: {author} · {at:dd.MM.yyyy HH:mm} · {count}")));
        Line(text, marking);

        return [.. Encoding.UTF8.GetPreamble(), .. Encoding.UTF8.GetBytes(text.ToString())];
    }

    /// <summary>Ячейка с текстом: формула — как текст (апостроф), разделитель, кавычки и переводы строк — в кавычках.</summary>
    public static string Escape(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        // Защита от формул в ячейке (CSV injection): Excel выполнил бы «=…» из названия дела.
        var safe = value[0] is '=' or '+' or '-' or '@' or '\t' or '\r' ? "'" + value : value;
        return safe.IndexOfAny([';', '"', '\r', '\n']) < 0 ? safe : Quote(safe);
    }

    /// <summary>
    /// Ячейка-номер как строковая константа Excel <c>="…"</c>: Excel показывает её текстом, а не датой. Кавычки внутри
    /// удваиваются — из константы нельзя «выйти» в формулу; затем ячейка целиком берётся в кавычки CSV.
    /// </summary>
    public static string TextConstant(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var inner = value.Replace("\r", " ", StringComparison.Ordinal).Replace("\n", " ", StringComparison.Ordinal)
            .Replace("\"", "\"\"", StringComparison.Ordinal);
        return Quote("=\"" + inner + "\"");
    }

    private static string Quote(string value) => "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";

    // Ячейки уже подготовлены (Escape / TextConstant / число) — здесь только разделитель и конец строки.
    private static void Line(StringBuilder text, params string[] cells) =>
        text.Append(string.Join(';', cells)).Append("\r\n");
}
