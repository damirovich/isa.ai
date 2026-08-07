using System.Globalization;
using PdfSharp.Drawing;
using PdfSharp.Pdf;
using PdfSharp.Pdf.IO;

namespace ISC.AI.Modules.DocFlow.Data.Reports;

/// <summary>
/// Нанесение отметки получателя на выдаваемый PDF («ФИО · дата и время», перенос СКИД).
/// </summary>
/// <remarks>
/// ЗАЧЕМ. Файл, покинувший систему, живёт своей жизнью: его печатают, пересылают, забывают на
/// принтере. Отметка привязывает конкретную копию к тому, кто её получил, и к моменту выдачи —
/// то же назначение, что у грифа в отчётах (ТБ-033), но для чужих, уже готовых документов.
/// Журнал фиксирует ФАКТ выдачи; отметка делает след видимым на самой бумаге.
///
/// ЧЕГО ЭТО НЕ ДЕЛАЕТ. Это не защита от копирования и не «водяной знак» в криптографическом смысле:
/// отметку можно удалить редактором. Она поднимает цену анонимной утечки, а не запрещает утечку —
/// обещать большее было бы обманом.
///
/// ПОЧЕМУ НЕ ВСЕГДА. Наносится только на PDF: другие форматы (docx, изображения) пришлось бы
/// конвертировать, а конвертация в закрытом контуре — отдельная зависимость, от которой уже
/// отказались (этап 4.3). Битый или защищённый паролем PDF отдаётся КАК ЕСТЬ: сорвать выдачу
/// документа из-за неудавшегося оформления хуже, чем выдать его без отметки.
/// </remarks>
public static class PdfWatermark
{
    /// <summary>Полупрозрачность отметки — читаемо, но не мешает читать сам документ.</summary>
    private const double Opacity = 0.35;

    /// <summary>Пытается нанести отметку; при любой неудаче возвращает исходные байты.</summary>
    /// <param name="content">Исходный PDF.</param>
    /// <param name="recipient">Кому выдан (ФИО либо имя входа).</param>
    /// <param name="issuedAt">Момент выдачи (местное время читателя).</param>
    /// <param name="fontOptions">Пути к файлу шрифта — те же, что у PDF-отчётов.</param>
    public static byte[] TryStamp(
        byte[] content, string recipient, DateTime issuedAt, PdfReportFontOptions fontOptions)
    {
        ArgumentNullException.ThrowIfNull(content);

        try
        {
            PdfReportFonts.Install(fontOptions);

            using var input = new MemoryStream(content);

            // Modify: документ открывается для правки. Если PDF защищён паролем или повреждён,
            // Open бросит — и мы отдадим исходник (см. catch ниже).
            using var document = PdfReader.Open(input, PdfDocumentOpenMode.Modify);

            var text = $"{recipient} · {issuedAt.ToString("dd.MM.yyyy HH:mm", CultureInfo.InvariantCulture)}";
            var font = new XFont(PdfReportFontResolver.FamilyName, 8, XFontStyleEx.Regular);

            foreach (var page in document.Pages)
            {
                // Append: рисуем ПОВЕРХ существующего содержимого, не затирая его.
                using var gfx = XGraphics.FromPdfPage(page, XGraphicsPdfPageOptions.Append);

                var brush = new XSolidBrush(XColor.FromArgb((int)(255 * Opacity), 120, 0, 0));

                // Отметка идёт в нижний колонтитул КАЖДОЙ страницы: вырванный лист не должен
                // терять привязку к получателю.
                gfx.DrawString(text, font, brush, 20, gfx.PageSize.Height - 12);
            }

            using var output = new MemoryStream();
            document.Save(output);
            return output.ToArray();
        }
        catch (Exception)
        {
            // Оформление НЕ должно ломать выдачу документа: пользователь пришёл за содержимым,
            // а не за отметкой. Факт выдачи всё равно зафиксирован журналом (ТБ-030).
            return content;
        }
    }
}
