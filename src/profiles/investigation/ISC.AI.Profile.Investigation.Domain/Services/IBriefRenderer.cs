namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Вид справки (ТФ-ОТЧ-01/02, ADR-0053).</summary>
public enum BriefKind
{
    /// <summary>Справка по делу (ТФ-ОТЧ-02).</summary>
    Case = 1,

    /// <summary>Справка по фигуранту (ТФ-ОТЧ-01).</summary>
    Person = 2,
}

/// <summary>Строка «поле — значение» раздела справки.</summary>
/// <param name="Label">Поле.</param>
/// <param name="Value">Значение как в системе.</param>
public sealed record BriefFact(string Label, string Value);

/// <summary>
/// Таблица раздела справки. <see cref="Rows"/> — показанные строки; <see cref="TotalRows"/> — сколько их всего: если
/// больше, справка прямо пишет, сколько не вошло (молчаливого обрезания нет).
/// </summary>
/// <param name="Columns">Заголовки колонок.</param>
/// <param name="Rows">Строки — по ячейке на колонку.</param>
/// <param name="TotalRows">Сколько строк всего.</param>
public sealed record BriefTable(IReadOnlyList<string> Columns, IReadOnlyList<IReadOnlyList<string>> Rows, int TotalRows)
{
    /// <summary>Сколько строк не вошло в таблицу.</summary>
    public int Omitted => Math.Max(0, TotalRows - Rows.Count);
}

/// <summary>Раздел справки: строки «поле — значение», таблица и пояснение.</summary>
/// <param name="Title">Заголовок раздела.</param>
/// <param name="Facts">Строки «поле — значение».</param>
/// <param name="Table">Таблица; <see langword="null"/> — без таблицы.</param>
/// <param name="Note">Пояснение к разделу (что не вошло, как читать); <see langword="null"/> — без пояснения.</param>
public sealed record BriefSection(string Title, IReadOnlyList<BriefFact> Facts, BriefTable? Table = null, string? Note = null)
{
    /// <summary>Раздел пуст: ни строк, ни строк таблицы.</summary>
    public bool IsEmpty => Facts.Count == 0 && (Table is null || Table.TotalRows == 0);
}

/// <summary>
/// Факты справки, собранные системой под решёткой субъекта (ADR-0053): ничего из них не пишет модель.
/// </summary>
/// <param name="Kind">Вид справки.</param>
/// <param name="TargetId">Дело или фигурант.</param>
/// <param name="CaseId">Дело (у справки по фигуранту — его дело).</param>
/// <param name="Title">Заголовок документа.</param>
/// <param name="Subtitle">Подзаголовок: дело, период.</param>
/// <param name="From">Начало периода (справка по делу); <see langword="null"/> — без периода.</param>
/// <param name="To">Конец периода (справка по делу); <see langword="null"/> — без периода.</param>
/// <param name="CollectedAtUtc">Когда собраны факты (UTC).</param>
/// <param name="Classification">Гриф справки — максимум грифов всех вошедших сведений (ТБ-033).</param>
/// <param name="DivisionId">Подразделение дела.</param>
/// <param name="Sections">Разделы.</param>
public sealed record Brief(
    BriefKind Kind,
    int TargetId,
    int CaseId,
    string Title,
    string Subtitle,
    DateOnly? From,
    DateOnly? To,
    DateTime CollectedAtUtc,
    short Classification,
    int DivisionId,
    IReadOnlyList<BriefSection> Sections);

/// <summary>Справка к выгрузке: факты, изложение и реквизиты документа (ТБ-033).</summary>
/// <param name="Brief">Факты справки.</param>
/// <param name="Summary">Краткое изложение (текст модели, проверенный человеком, без разметки); <see langword="null"/> — без изложения.</param>
/// <param name="ClassificationMarking">Маркировка грифа.</param>
/// <param name="Executor">Исполнитель — имя сотрудника.</param>
/// <param name="DraftNotice">Пометка черновика.</param>
/// <param name="SummaryNotice">Пометка к изложению: кем подготовлено.</param>
public sealed record BriefExport(
    Brief Brief, string? Summary, string ClassificationMarking, string Executor, string DraftNotice, string SummaryNotice);

/// <summary>Отрисовка справки в .docx (ADR-0053): гриф первой и последней строкой и в свойствах файла, учётный номер,
/// пометка черновика, таблицы фактов, изложение, исполнитель.</summary>
public interface IBriefRenderer
{
    /// <summary>Файл .docx справки.</summary>
    byte[] RenderDocx(BriefExport document);
}
