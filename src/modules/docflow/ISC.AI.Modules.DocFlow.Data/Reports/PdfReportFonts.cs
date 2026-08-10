using PdfSharp.Fonts;

namespace ISC.AI.Modules.DocFlow.Data.Reports;

/// <summary>
/// Пути к файлам шрифта для PDF-отчётов (<c>DocFlow:Reports:PdfFont:Regular</c> и <c>:Bold</c>).
/// </summary>
/// <remarks>
/// Шрифт НЕ вшивается в сборку и НЕ скачивается: контур изолирован (инвариант air-gap), а раскладывать
/// чужие шрифты вместе с кодом нельзя из-за их лицензий. Поэтому берём файл, уже установленный
/// в системе; путь задаётся конфигурацией, чтобы стенд на Windows и сервер на Linux не требовали
/// разной сборки. Пустые значения означают «искать среди известных мест» (см. <see cref="PdfReportFonts"/>).
/// </remarks>
public sealed record PdfReportFontOptions(string? RegularPath, string? BoldPath);

/// <summary>
/// Резолвер шрифта PDFsharp поверх файлов файловой системы.
/// </summary>
/// <remarks>
/// PDFsharp без резолвера не находит ни одного шрифта в .NET на Linux и падает уже на первой строке.
/// Кириллица дополнительно сужает выбор: подходит только шрифт с русскими глифами, иначе отчёт
/// печатается пустыми прямоугольниками — и это заметно уже после выдачи файла пользователю.
/// </remarks>
internal sealed class PdfReportFontResolver : IFontResolver
{
    /// <summary>Имя семейства, под которым шрифт виден коду отрисовки.</summary>
    public const string FamilyName = "ISCAI Report";

    private const string RegularFace = FamilyName + "#regular";
    private const string BoldFace = FamilyName + "#bold";

    private readonly byte[] _regular;
    private readonly byte[] _bold;

    public PdfReportFontResolver(PdfReportFontOptions options)
    {
        var (regularPath, boldPath) = PdfReportFontLocator.Locate(options);

        _regular = File.ReadAllBytes(regularPath);

        // Жирного начертания может не быть отдельным файлом — тогда «жирным» рисуем обычным.
        // Отчёт при этом остаётся читаемым, а падать из-за оформления неправильно.
        _bold = File.Exists(boldPath) ? File.ReadAllBytes(boldPath) : _regular;
    }

    /// <inheritdoc />
    public FontResolverInfo? ResolveTypeface(string familyName, bool isBold, bool isItalic) =>
        new(isBold ? BoldFace : RegularFace);

    /// <inheritdoc />
    public byte[]? GetFont(string faceName) =>
        faceName == BoldFace ? _bold : _regular;
}

/// <summary>
/// Поиск файла шрифта: явно заданный путь либо известные места установки.
/// </summary>
/// <remarks>
/// Вынесен отдельно от резолвера намеренно: резолвер попадает в ГЛОБАЛЬНОЕ состояние PDFsharp
/// и ставится один раз на процесс, а поиск пути — чистая функция, которую можно проверить тестом
/// сколько угодно раз и без побочных эффектов.
/// </remarks>
public static class PdfReportFontLocator
{
    /// <summary>Ключ настройки — упоминается в сообщениях об ошибке, чтобы админ знал, что править.</summary>
    public const string SettingKey = "DocFlow:Reports:PdfFont:Regular";

    // Известные места установки шрифтов с кириллицей. Windows — Arial (есть всегда),
    // Linux — DejaVu и Liberation из стандартных пакетов дистрибутивов.
    private static readonly (string Regular, string Bold)[] KnownFonts =
    [
        (@"C:\Windows\Fonts\arial.ttf", @"C:\Windows\Fonts\arialbd.ttf"),
        (@"C:\Windows\Fonts\times.ttf", @"C:\Windows\Fonts\timesbd.ttf"),
        ("/usr/share/fonts/truetype/dejavu/DejaVuSans.ttf", "/usr/share/fonts/truetype/dejavu/DejaVuSans-Bold.ttf"),
        ("/usr/share/fonts/dejavu/DejaVuSans.ttf", "/usr/share/fonts/dejavu/DejaVuSans-Bold.ttf"),
        ("/usr/share/fonts/truetype/liberation/LiberationSans-Regular.ttf", "/usr/share/fonts/truetype/liberation/LiberationSans-Bold.ttf"),
        ("/usr/share/fonts/liberation-sans/LiberationSans-Regular.ttf", "/usr/share/fonts/liberation-sans/LiberationSans-Bold.ttf"),
    ];

    /// <summary>Есть ли в системе подходящий шрифт (без учёта явной настройки).</summary>
    public static bool AnyInstalled => KnownFonts.Any(font => File.Exists(font.Regular));

    /// <summary>Находит файлы шрифта; бросает <see cref="FileNotFoundException"/>, если их нет.</summary>
    public static (string Regular, string Bold) Locate(PdfReportFontOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.RegularPath))
        {
            // Путь задан явно — молча подменять его найденным «где-то ещё» нельзя: администратор
            // выбрал конкретный шрифт (например, единственный разрешённый в организации).
            if (!File.Exists(options.RegularPath))
            {
                throw new FileNotFoundException(
                    $"Шрифт для PDF-отчётов не найден: «{options.RegularPath}». "
                    + $"Проверьте настройку {SettingKey}.", options.RegularPath);
            }

            return (options.RegularPath, options.BoldPath ?? options.RegularPath);
        }

        foreach (var (regular, bold) in KnownFonts)
        {
            if (File.Exists(regular))
            {
                return (regular, bold);
            }
        }

        throw new FileNotFoundException(
            "Не найден шрифт с кириллицей для PDF-отчётов. Установите шрифты (например, пакет "
            + $"fonts-dejavu) или укажите путь к файлу .ttf в настройке {SettingKey}.");
    }
}

/// <summary>Однократная установка резолвера шрифта в глобальные настройки PDFsharp.</summary>
internal static class PdfReportFonts
{
    private static readonly Lock Gate = new();
    private static bool _installed;

    /// <summary>
    /// Ставит резолвер, если он ещё не поставлен.
    /// </summary>
    /// <remarks>
    /// <c>GlobalFontSettings.FontResolver</c> у PDFsharp — ГЛОБАЛЬНОЕ статическое состояние на процесс,
    /// а не свойство документа. Повторная установка после первого использования шрифта считается
    /// ошибкой, поэтому ставим ровно один раз под замком: отчёты формируются параллельно, и без
    /// синхронизации два одновременных запроса гонялись бы за одним полем.
    /// </remarks>
    public static void Install(PdfReportFontOptions options)
    {
        if (_installed)
        {
            return;
        }

        lock (Gate)
        {
            if (_installed)
            {
                return;
            }

            GlobalFontSettings.FontResolver = new PdfReportFontResolver(options);
            _installed = true;
        }
    }
}
