using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Application.Features.Import;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media.Import;

/// <summary>Опись архива (ADR-0041 п. 2): CSV из Excel — разделитель, кодировка, кавычки, заголовки, даты, пределы.</summary>
public sealed class ImportManifestTests
{
    private static readonly TimeZoneInfo Bishkek = TimeZoneInfo.CreateCustomTimeZone("UTC+6", TimeSpan.FromHours(6), "UTC+6", "UTC+6");

    private static byte[] Utf8(string text, bool bom = false) =>
        (bom ? new byte[] { 0xEF, 0xBB, 0xBF } : []).Concat(Encoding.UTF8.GetBytes(text)).ToArray();

    [Fact(DisplayName = "Excel в русской Windows: «;», UTF-8 с BOM, заголовки по-русски без учёта регистра и «ё»")]
    public void Reads_semicolon_utf8_with_bom()
    {
        var result = ImportManifest.Read(Utf8("Файл;ДЕЛО;Дата съёмки;Источник\r\nдело-1/a.jpg;12/2023;14.05.2023 13:45;камера 3\r\n", bom: true));

        result.Error.ShouldBeNull();
        result.Delimiter.ShouldBe(';');
        result.EncodingName.ShouldBe("UTF-8");
        result.Rows.ShouldHaveSingleItem();
        var row = result.Rows[0];
        row.RowNumber.ShouldBe(2);
        row.File.ShouldBe("дело-1/a.jpg");
        row.CaseNumber.ShouldBe("12/2023");
        row.CapturedAt.ShouldBe("14.05.2023 13:45");
        row.Source.ShouldBe("камера 3");
        result.Sha256.Length.ShouldBe(64);
    }

    [Fact(DisplayName = "Windows-1251 (CSV без UTF-8 из русского Excel) распознаётся по невалидному UTF-8")]
    public void Reads_windows_1251()
    {
        var cp1251 = System.Text.CodePagesEncodingProvider.Instance.GetEncoding(1251)!;
        var result = ImportManifest.Read(cp1251.GetBytes("файл;дело;место\nвидео.mp4;7;ул. Ленина\n"));

        result.Error.ShouldBeNull();
        result.EncodingName.ShouldBe("Windows-1251");
        result.Rows[0].File.ShouldBe("видео.mp4");
        result.Rows[0].Place.ShouldBe("ул. Ленина");
    }

    [Theory(DisplayName = "Разделитель — по заголовку: «,» и табуляция")]
    [InlineData(',')]
    [InlineData('\t')]
    public void Detects_delimiter(char delimiter)
    {
        var text = $"file{delimiter}case{delimiter}sha256\na.jpg{delimiter}1{delimiter}\n";
        var result = ImportManifest.Read(Utf8(text));

        result.Error.ShouldBeNull();
        result.Delimiter.ShouldBe(delimiter);
        result.Rows[0].File.ShouldBe("a.jpg");
        result.Rows[0].CaseNumber.ShouldBe("1");
        result.Rows[0].Sha256.ShouldBeNull();
    }

    [Fact(DisplayName = "Кавычки по RFC 4180: разделитель, перевод строки и удвоенная кавычка внутри поля")]
    public void Handles_quoted_fields()
    {
        var result = ImportManifest.Read(Utf8("файл;дело;источник\n\"папка;1/a.jpg\";5;\"сказал \"\"стоп\"\"\nвторая строка\"\nb.jpg;6;\n"));

        result.Error.ShouldBeNull();
        result.Rows.Count.ShouldBe(2);
        result.Rows[0].File.ShouldBe("папка;1/a.jpg");
        result.Rows[0].Source.ShouldBe("сказал \"стоп\"\nвторая строка");
        result.Rows[1].File.ShouldBe("b.jpg");
    }

    [Fact(DisplayName = "Пустые строки пропускаются, номер строки описи сохраняется; лишние столбцы перечисляются")]
    public void Skips_empty_rows_and_lists_ignored_columns()
    {
        var result = ImportManifest.Read(Utf8("файл;дело;примечание\n\n;;\na.jpg;1;проверить\n"));

        result.Rows.ShouldHaveSingleItem().RowNumber.ShouldBe(4);
        result.IgnoredColumns.ShouldBe(["примечание"]);
    }

    [Fact(DisplayName = "Без столбцов «файл» и «дело» опись не читается — понятная ошибка, строк нет")]
    public void Requires_file_and_case_columns()
    {
        var result = ImportManifest.Read(Utf8("путь к видео;номер\na.mp4;1\n"));

        result.Error.ShouldNotBeNull().ShouldContain("«файл» и «дело»");
        result.Rows.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Пустая опись — ошибка")]
    public void Empty_manifest_is_an_error() =>
        ImportManifest.Read([]).Error.ShouldNotBeNull();

    [Fact(DisplayName = "Больше предела строк — ошибка «разделите опись»")]
    public void Too_many_rows_is_an_error()
    {
        var text = new StringBuilder("файл;дело\n");
        for (var i = 0; i <= ImportManifest.MaxRows; i++)
        {
            text.Append("f").Append(i).Append(".jpg;1\n");
        }

        ImportManifest.Read(Utf8(text.ToString())).Error.ShouldNotBeNull().ShouldContain("разделите");
    }

    [Theory(DisplayName = "Дата съёмки: дд.мм.гггг чч:мм[:сс], гггг-мм-дд, ISO со смещением; без смещения — пояс сервера")]
    [InlineData("14.05.2023 13:45", "2023-05-14T07:45:00Z", false)]
    [InlineData("4.5.2023 9:05:30", "2023-05-04T03:05:30Z", false)]
    [InlineData("2023-05-14 13:45", "2023-05-14T07:45:00Z", false)]
    [InlineData("2023-05-14T13:45:00+03:00", "2023-05-14T10:45:00Z", false)]
    [InlineData("2023-05-14T07:45:00Z", "2023-05-14T07:45:00Z", false)]
    [InlineData("14.05.2023", "2023-05-13T18:00:00Z", true)]
    [InlineData("2023-05-14", "2023-05-13T18:00:00Z", true)]
    public void Parses_capture_time(string text, string expectedUtc, bool dateOnly)
    {
        ImportManifest.TryParseCapturedAt(text, Bishkek, out var value, out var onlyDate).ShouldBeTrue();
        value.UtcDateTime.ShouldBe(DateTimeOffset.Parse(expectedUtc, System.Globalization.CultureInfo.InvariantCulture).UtcDateTime);
        onlyDate.ShouldBe(dateOnly);
    }

    [Theory(DisplayName = "Неразборчивая дата — не дата")]
    [InlineData("вчера")]
    [InlineData("31.02.2023")]
    [InlineData("2023/14/40")]
    public void Rejects_bad_capture_time(string text) =>
        ImportManifest.TryParseCapturedAt(text, Bishkek, out _, out _).ShouldBeFalse();

    [Theory(DisplayName = "SHA-256: 64 шестнадцатеричных символа, регистр приводится к верхнему; иное — не контрольная сумма")]
    [InlineData("ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", "AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34AB12CD34")]
    [InlineData("ab12", null)]
    [InlineData("zz12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34ab12cd34", null)]
    public void Normalizes_sha256(string text, string? expected) =>
        ImportManifest.NormalizeSha256(text).ShouldBe(expected);

    [Fact(DisplayName = "Запись и чтение: опись возвращается такой же; «формула» Excel экранирована апострофом и снимается при чтении")]
    public async Task Write_then_read_round_trips()
    {
        ManifestRow[] rows =
        [
            new(2, "=cmd|' /C calc'!A0.jpg", "-12", "07", "14.05.2023 13:45", "источник; с разделителем", "место \"в кавычках\"", null),
            new(3, "папка/b.mp4", "12/2023"),
        ];

        using var output = new MemoryStream();
        await ImportManifest.WriteAsync(output, ToAsync(rows));
        var bytes = output.ToArray();

        bytes.Take(3).ShouldBe(new byte[] { 0xEF, 0xBB, 0xBF }, "UTF-8 с BOM — Excel откроет кириллицу без вопросов");
        Encoding.UTF8.GetString(bytes).ShouldContain("'=cmd", Case.Sensitive, "формула экранирована");

        var read = ImportManifest.Read(bytes);
        read.Error.ShouldBeNull();
        read.Rows.Select(r => (r.File, r.CaseNumber, r.DivisionCode, r.CapturedAt, r.Source, r.Place)).ShouldBe(
        [
            ("=cmd|' /C calc'!A0.jpg", "-12", "07", "14.05.2023 13:45", "источник; с разделителем", "место \"в кавычках\""),
            ("папка/b.mp4", "12/2023", null, null, null, null),
        ]);
    }

    private static async IAsyncEnumerable<ManifestRow> ToAsync(IEnumerable<ManifestRow> rows)
    {
        foreach (var row in rows)
        {
            yield return row;
        }

        await Task.CompletedTask;
    }
}
