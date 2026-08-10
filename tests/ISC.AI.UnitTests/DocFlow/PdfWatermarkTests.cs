using System;
using System.Text;
using ISC.AI.Modules.DocFlow.Data.Reports;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Отметка получателя на выдаваемых PDF.
/// </summary>
/// <remarks>
/// Главное свойство здесь — НЕ «знак нанесён», а «выдача не сорвана». Пользователь пришёл за
/// содержимым документа; если оформление не удалось (битый файл, PDF под паролем, нет шрифта),
/// он обязан получить файл как есть. Факт выдачи в любом случае зафиксирован журналом (ТБ-030).
/// </remarks>
public sealed class PdfWatermarkTests
{
    private static readonly PdfReportFontOptions Fonts = new(null, null);

    [Fact(DisplayName = "Не-PDF (битые байты) отдаётся без изменений, а не роняет выдачу")]
    public void Broken_input_is_returned_unchanged()
    {
        var garbage = Encoding.UTF8.GetBytes("это не pdf");

        var result = PdfWatermark.TryStamp(garbage, "Иванов И.", DateTime.Now, Fonts);

        result.ShouldBe(garbage);
    }

    [Fact(DisplayName = "Пустой файл не приводит к исключению")]
    public void Empty_input_is_safe()
    {
        var result = PdfWatermark.TryStamp([], "Иванов И.", DateTime.Now, Fonts);

        result.ShouldBeEmpty();
    }

    [Fact(DisplayName = "На настоящий PDF отметка наносится, файл остаётся читаемым PDF")]
    public void Real_pdf_gets_stamped()
    {
        // Шрифт берётся из системы; там, где его нет, проверять нечего — см. PdfReportFontLocator.
        if (!PdfReportFontLocator.AnyInstalled)
        {
            return;
        }

        // Исходный PDF делаем тем же рендерером отчётов — отдельный образец в репозитории не нужен.
        var source = new PdfReportRenderer(Fonts).Render(SampleView).Content;

        var stamped = PdfWatermark.TryStamp(source, "Иванов Иван", new DateTime(2026, 8, 7, 10, 0, 0), Fonts);

        Encoding.ASCII.GetString(stamped, 0, 4).ShouldBe("%PDF");

        // Содержимое изменилось (что-то дорисовано) и файл не опустел.
        stamped.ShouldNotBe(source);
        stamped.Length.ShouldBeGreaterThan(1000);
    }

    private static ReportView SampleView { get; } = new(
        "Проверочный документ",
        "Открыто",
        "Сформировал: тест.",
        ["Период: 01.01.2026 — 31.12.2026"],
        ["Колонка"],
        [new ReportViewGroup("Группа", [new ReportViewRow(["значение"])], [])],
        []);
}
