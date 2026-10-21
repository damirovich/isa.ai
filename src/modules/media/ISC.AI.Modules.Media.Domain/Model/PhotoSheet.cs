using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>Прямоугольник на листе в миллиметрах от левого верхнего угла.</summary>
/// <param name="X">Левый край.</param>
/// <param name="Y">Верхний край.</param>
/// <param name="Width">Ширина.</param>
/// <param name="Height">Высота.</param>
public sealed record SheetRect(double X, double Y, double Width, double Height);

/// <summary>Ячейка листа: снимок <paramref name="Index"/> (по порядку выбора), место снимка и полоса подписи под ним.</summary>
/// <param name="Index">Номер снимка в выгрузке, с нуля.</param>
/// <param name="Image">Прямоугольник, в который вписывается снимок с сохранением пропорций.</param>
/// <param name="Caption">Полоса подписи под снимком той же ширины.</param>
public sealed record PhotoSheetCell(int Index, SheetRect Image, SheetRect Caption);

/// <summary>Лист фототаблицы: номер (с единицы) и ячейки.</summary>
/// <param name="Number">Номер листа.</param>
/// <param name="Cells">Ячейки по порядку снимков.</param>
public sealed record PhotoSheetPage(int Number, IReadOnlyList<PhotoSheetCell> Cells);

/// <summary>Размер одного снимка на листе в миллиметрах [АИС ФТ-5.6.4: «в произвольном размере»].</summary>
/// <param name="WidthMm">Ширина.</param>
/// <param name="HeightMm">Высота.</param>
public sealed record PhotoSheetSize(double WidthMm, double HeightMm);

/// <summary>Снимок для листа: изображение (уже ужатое для печати), подпись, цвета текста и подложки (<c>#RRGGBB</c>).</summary>
/// <param name="Image">Изображение (JPEG или PNG).</param>
/// <param name="Caption">Подпись; пустая — полоса не рисуется.</param>
/// <param name="TextColor">Цвет текста подписи.</param>
/// <param name="BackgroundColor">Цвет подложки подписи.</param>
public sealed record PhotoSheetPhoto(byte[] Image, string? Caption, string TextColor, string BackgroundColor);

/// <summary>
/// Фототаблица к формированию (ТФ-МЕД-20, ADR-0057): снимки, раскладка, маркировка грифа, исполнитель и момент выдачи
/// (ТБ-033, ТБ-076).
/// </summary>
/// <param name="Photos">Снимки по порядку.</param>
/// <param name="PerPage">Снимков на лист — одно из <see cref="PhotoSheetLayout.PerPageOptions"/>.</param>
/// <param name="SingleSize">Размер снимка при одном снимке на лист; <see langword="null"/> — на весь лист.</param>
/// <param name="Title">Заголовок листа.</param>
/// <param name="Marking">Маркировка грифа — вверху и внизу каждой страницы и в свойствах файла.</param>
/// <param name="Executor">Кто выдал — в подвале и водяном знаке.</param>
/// <param name="IssuedAt">Когда выдано (местное время).</param>
public sealed record PhotoSheetDocument(
    IReadOnlyList<PhotoSheetPhoto> Photos,
    int PerPage,
    PhotoSheetSize? SingleSize,
    string Title,
    string Marking,
    string Executor,
    DateTime IssuedAt)
{
    /// <summary>Подпись выдачи — подвал и водяной знак: «ФИО · дд.мм.гггг чч:мм».</summary>
    public string IssuedBy => string.Create(CultureInfo.InvariantCulture, $"{Executor} · {IssuedAt:dd.MM.yyyy HH:mm}");
}

/// <summary>
/// Раскладка фототаблицы на листе A4 (ADR-0057 п. 2): допустимые сетки, поля, полосы маркировки, ячейки и листы.
/// Чистая функция — одна и та же для отрисовки PDF и для проверок.
/// </summary>
public static partial class PhotoSheetLayout
{
    /// <summary>Ширина листа A4, мм.</summary>
    public const double PageWidthMm = 210;

    /// <summary>Высота листа A4, мм.</summary>
    public const double PageHeightMm = 297;

    /// <summary>Поле листа, мм.</summary>
    public const double MarginMm = 12;

    /// <summary>Полоса маркировки сверху (гриф, заголовок, номер листа), мм.</summary>
    public const double HeaderMm = 12;

    /// <summary>Полоса маркировки снизу (гриф, «Выдал»), мм.</summary>
    public const double FooterMm = 14;

    /// <summary>Промежуток между ячейками, мм.</summary>
    public const double GapMm = 4;

    /// <summary>Наибольшее число снимков за одну выгрузку (5 листов по 20).</summary>
    public const int MaxPhotos = 100;

    /// <summary>Наибольшая длина подписи снимка.</summary>
    public const int MaxCaptionLength = 200;

    /// <summary>Наибольшая длина заголовка листа.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Наименьшая сторона снимка «в произвольном размере», мм.</summary>
    public const double MinSingleMm = 20;

    /// <summary>Заголовок листа по умолчанию.</summary>
    public const string DefaultTitle = "Фототаблица";

    /// <summary>Снимков на лист [АИС ФТ-5.6.4]: 1, 2, 4, 6, 9, 12, 16, 20.</summary>
    public static IReadOnlyList<int> PerPageOptions { get; } = [1, 2, 4, 6, 9, 12, 16, 20];

    /// <summary>Печатаемая область под снимки: лист без полей и полос маркировки.</summary>
    public static SheetRect ContentArea { get; } = new(
        MarginMm, MarginMm + HeaderMm, PageWidthMm - (2 * MarginMm), PageHeightMm - (2 * MarginMm) - HeaderMm - FooterMm);

    /// <summary>Наибольшая ширина снимка «в произвольном размере», мм.</summary>
    public static double MaxSingleWidthMm => ContentArea.Width;

    /// <summary>Наибольшая высота снимка «в произвольном размере» (под ним — полоса подписи), мм.</summary>
    public static double MaxSingleHeightMm => ContentArea.Height - CaptionHeightMm(1);

    /// <summary>Сетка «столбцы × строки» для числа снимков на лист.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Число не из <see cref="PerPageOptions"/>.</exception>
    public static (int Columns, int Rows) Grid(int perPage) => perPage switch
    {
        1 => (1, 1),
        2 => (1, 2),
        4 => (2, 2),
        6 => (2, 3),
        9 => (3, 3),
        12 => (3, 4),
        16 => (4, 4),
        20 => (4, 5),
        _ => throw new ArgumentOutOfRangeException(nameof(perPage), perPage, "Снимков на лист: 1, 2, 4, 6, 9, 12, 16 или 20."),
    };

    /// <summary>Высота полосы подписи, мм: крупнее на крупных снимках, мельче на мелкой сетке.</summary>
    public static double CaptionHeightMm(int perPage) => perPage switch
    {
        <= 2 => 10,
        <= 6 => 8,
        _ => 6,
    };

    /// <summary>Число листов для <paramref name="photos"/> снимков.</summary>
    public static int PageCount(int photos, int perPage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(photos);
        _ = Grid(perPage);
        return (photos + perPage - 1) / perPage;
    }

    /// <summary>Подходит ли размер «одного снимка на лист»: обе стороны в пределах печатаемой области.</summary>
    public static bool FitsSingle(PhotoSheetSize size) =>
        size is { WidthMm: >= MinSingleMm, HeightMm: >= MinSingleMm }
        && size.WidthMm <= MaxSingleWidthMm
        && size.HeightMm <= MaxSingleHeightMm;

    /// <summary>
    /// Листы и ячейки для <paramref name="photos"/> снимков. При одном снимке на лист с заданным
    /// <paramref name="singleSize"/> снимок занимает этот размер по центру сверху области, иначе — ячейку сетки.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Число на лист не из перечня или размер вне печатаемой области.</exception>
    public static IReadOnlyList<PhotoSheetPage> Pages(int photos, int perPage, PhotoSheetSize? singleSize = null)
    {
        var (columns, rows) = Grid(perPage);
        if (singleSize is not null && (perPage != 1 || !FitsSingle(singleSize)))
        {
            throw new ArgumentOutOfRangeException(nameof(singleSize), "Произвольный размер — только при одном снимке на лист и в пределах листа.");
        }

        var area = ContentArea;
        var caption = CaptionHeightMm(perPage);
        var cellWidth = (area.Width - (GapMm * (columns - 1))) / columns;
        var cellHeight = (area.Height - (GapMm * (rows - 1))) / rows;
        var pages = new List<PhotoSheetPage>();
        for (var page = 0; page < PageCount(photos, perPage); page++)
        {
            var cells = new List<PhotoSheetCell>();
            for (var slot = 0; slot < perPage && (page * perPage) + slot < photos; slot++)
            {
                var index = (page * perPage) + slot;
                if (singleSize is { } size)
                {
                    var x = area.X + ((area.Width - size.WidthMm) / 2);
                    cells.Add(new PhotoSheetCell(index, new SheetRect(x, area.Y, size.WidthMm, size.HeightMm),
                        new SheetRect(x, area.Y + size.HeightMm, size.WidthMm, caption)));
                    continue;
                }

                var left = area.X + ((slot % columns) * (cellWidth + GapMm));
                var top = area.Y + ((slot / columns) * (cellHeight + GapMm));
                cells.Add(new PhotoSheetCell(index, new SheetRect(left, top, cellWidth, cellHeight - caption),
                    new SheetRect(left, top + cellHeight - caption, cellWidth, caption)));
            }

            pages.Add(new PhotoSheetPage(page + 1, cells));
        }

        return pages;
    }

    /// <summary>
    /// Место снимка внутри прямоугольника: вписан с сохранением пропорций и по центру (не растянут и не обрезан).
    /// </summary>
    public static SheetRect Fit(SheetRect box, int imageWidth, int imageHeight)
    {
        ArgumentNullException.ThrowIfNull(box);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(imageHeight, 1);

        var scale = Math.Min(box.Width / imageWidth, box.Height / imageHeight);
        var width = imageWidth * scale;
        var height = imageHeight * scale;
        return new SheetRect(box.X + ((box.Width - width) / 2), box.Y + ((box.Height - height) / 2), width, height);
    }

    /// <summary>Размер в точках изображения при 300 точках на дюйм — для ужатия снимка под ячейку.</summary>
    public static int PixelsAt300Dpi(double millimetres) => Math.Max(1, (int)Math.Ceiling(millimetres / 25.4 * 300));

    /// <summary>Цвет в виде <c>#RRGGBB</c>.</summary>
    public static bool IsColor(string? value) => value is not null && ColorPattern().IsMatch(value);

    [GeneratedRegex("^#[0-9A-Fa-f]{6}$")]
    private static partial Regex ColorPattern();
}
