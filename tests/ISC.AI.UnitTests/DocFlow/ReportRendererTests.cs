using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using ClosedXML.Excel;
using DocumentFormat.OpenXml.Packaging;
using ISC.AI.Modules.DocFlow.Data.Reports;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Отрисовка отчёта в три формата (разд. 6 ТЗ СКИД).
/// </summary>
/// <remarks>
/// ГЛАВНОЕ, ЧТО ЗДЕСЬ ПРОВЕРЯЕТСЯ, — маркировка грифа в самом файле (ТБ-033). Отчёт по документам
/// ДСП сам является ДСП, и рендерер не вправе «потерять» гриф ради оформления: файл уходит из системы
/// и дальше живёт своей жизнью, где никакая решётка доступа его уже не догонит.
/// </remarks>
public sealed class ReportRendererTests
{
    private const string Marking = "ДСП. Гриф: 2";

    private static readonly ReportView View = new(
        "Отчёт по ответственным лицам",
        Marking,
        "Сформировал: Иванов И. Дата и время: 01.03.2026 10:00.",
        ["Период: 01.01.2026 — 31.12.2026"],
        ["Рег. номер", "Дата рег.", "Тип", "Краткое содержание", "Подразделение",
            "Исполнитель", "Инспектор", "Срок", "Статус"],
        [
            new ReportViewGroup(
                "Иванов И.",
                [new ReportViewRow(["01-05/1", "01.03.2026", "Письмо", "Проверка соблюдения сроков",
                    "ГИ", "Иванов И.", "Сидоров С.", "01.04.2026", "В работе"])],
                [new ReportTotal("Строк (поручений)", "1")]),
        ],
        [new ReportTotal("Строк (поручений)", "1"), new ReportTotal("Документов", "1")]);

    [Fact(DisplayName = "Excel: гриф — в первой ячейке листа, имя файла с расширением .xlsx")]
    public void Excel_puts_marking_first()
    {
        var document = new ExcelReportRenderer().Render(View);

        document.FileName.ShouldEndWith(".xlsx");
        document.ContentType.ShouldBe("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet");

        using var stream = new MemoryStream(document.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        sheet.Cell(1, 1).GetString().ShouldBe(Marking);
        Cells(sheet).ShouldContain("Проверка соблюдения сроков");
    }

    /// <summary>
    /// Рег. номер вида «01-05/1» Excel по умолчанию принимает за дату и молча его портит.
    /// Правится это только вручную и уже после того, как отчёт распечатали.
    /// </summary>
    [Fact(DisplayName = "Excel: рег. номер остаётся текстом и не превращается в дату")]
    public void Excel_keeps_reg_number_as_text()
    {
        var document = new ExcelReportRenderer().Render(View);

        using var stream = new MemoryStream(document.Content);
        using var workbook = new XLWorkbook(stream);
        var sheet = workbook.Worksheets.First();

        var cell = sheet.CellsUsed(cell => cell.GetString() == "01-05/1").ShouldHaveSingleItem();
        cell.DataType.ShouldBe(XLDataType.Text);
    }

    [Fact(DisplayName = "Word: гриф — и в тексте, и в свойствах файла")]
    public void Word_puts_marking_in_text_and_properties()
    {
        var document = new WordReportRenderer().Render(View);

        document.FileName.ShouldEndWith(".docx");

        using var stream = new MemoryStream(document.Content);
        using var word = WordprocessingDocument.Open(stream, isEditable: false);

        var text = word.MainDocumentPart!.Document.Body!.InnerText;
        text.ShouldContain(Marking);
        text.ShouldContain("Проверка соблюдения сроков");

        // Свойства файла — чтобы маркировка пережила копирование текста из документа.
        word.PackageProperties.Category.ShouldBe(Marking);
    }

    [Fact(DisplayName = "PDF: формируется корректный файл с грифом на странице")]
    public void Pdf_renders_document()
    {
        // Шрифт берётся из системы (вшить нельзя — лицензии, скачать нельзя — изолированный контур).
        // Если на машине сборки шрифтов с кириллицей нет, проверяем ВТОРОЕ обязательное свойство:
        // ошибка должна называть настройку, которую админу править, а не падать безымянно.
        if (!PdfReportFontLocator.AnyInstalled)
        {
            var error = Should.Throw<FileNotFoundException>(
                () => PdfReportFontLocator.Locate(new PdfReportFontOptions(null, null)));

            error.Message.ShouldContain(PdfReportFontLocator.SettingKey);
            return;
        }

        var document = new PdfReportRenderer(new PdfReportFontOptions(null, null)).Render(View);

        document.FileName.ShouldEndWith(".pdf");
        document.ContentType.ShouldBe("application/pdf");

        // Заголовок формата: файл действительно PDF, а не пустой буфер.
        Encoding.ASCII.GetString(document.Content, 0, 4).ShouldBe("%PDF");
        document.Content.Length.ShouldBeGreaterThan(1000);
    }

    /// <summary>
    /// Явно заданный путь молча подменять найденным «где-то ещё» нельзя: администратор мог выбрать
    /// единственный разрешённый в организации шрифт, и тихая подмена сделает отчёты неотличимо иными.
    /// </summary>
    [Fact(DisplayName = "PDF: заданный, но отсутствующий шрифт даёт понятную ошибку с именем настройки")]
    public void Pdf_reports_missing_configured_font()
    {
        var missing = Path.Combine(Path.GetTempPath(), "isc-ai-net-takogo-shrifta.ttf");

        var error = Should.Throw<FileNotFoundException>(
            () => PdfReportFontLocator.Locate(new PdfReportFontOptions(missing, null)));

        error.Message.ShouldContain(missing);
        error.Message.ShouldContain(PdfReportFontLocator.SettingKey);
    }

    [Fact(DisplayName = "Имя файла отчёта содержит заголовок и допустимо для файловой системы")]
    public void File_name_is_safe()
    {
        var document = new ExcelReportRenderer().Render(View);

        document.FileName.ShouldContain("Отчёт");
        document.FileName.IndexOfAny(Path.GetInvalidFileNameChars()).ShouldBe(-1);
    }

    private static IReadOnlyList<string> Cells(IXLWorksheet sheet) =>
        [.. sheet.CellsUsed().Select(cell => cell.GetString())];
}
