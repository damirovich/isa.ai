using System;
using System.Text.RegularExpressions;
using ISC.AI.Modules.Media.Domain.Model;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Безопасное расширение имени файла носителя: путь к файлу уходит ffmpeg/ffprobe текстом командной строки, и
/// кавычка или «\» в расширении, пришедшем с изъятого устройства, разорвали бы её (лишние аргументы на Linux).
/// </summary>
public sealed class MediaFileNamesTests
{
    // Формат имени, которое принимает раздача файлов носителей (MediaFileEndpoints): GUID + расширение 2–5.
    private static readonly Regex StoredFileNamePattern = new(@"^[0-9a-fA-F]{32}\.[A-Za-z0-9]{2,5}$");

    [Theory(DisplayName = "Расширение из 2–5 латинских букв и цифр сохраняется (регистр тоже); всё прочее — .bin")]
    [InlineData("voice.m4a", ".m4a")]
    [InlineData("clip.MP4", ".MP4")]
    [InlineData("voice.3gpp", ".3gpp")]
    [InlineData("photo.jpeg", ".jpeg")]
    [InlineData("путь/к/записи.ogg", ".ogg")]
    [InlineData("noext", ".bin")]
    [InlineData("", ".bin")]
    [InlineData(null, ".bin")]
    [InlineData("x.", ".bin")]
    [InlineData("x.a", ".bin")]
    [InlineData("x.abcdef", ".bin")]
    [InlineData("запись.m4a\" -y \"out", ".bin")]
    [InlineData("x.m4\"a", ".bin")]
    [InlineData("x.m4a\\", ".bin")]
    [InlineData("x.mp3 ", ".bin")]
    [InlineData("x.мп3", ".bin")]
    [InlineData("x.m-4a", ".bin")]
    public void Safe_extension_keeps_only_short_ascii_alphanumerics(string? fileName, string expected)
    {
        var extension = MediaFileNames.SafeExtension(fileName);

        extension.ShouldBe(expected);
        extension.ShouldNotContain("\"");
        extension.ShouldNotContain("\\");

        // Любой результат годится для раздачи файлов: сохранённый носитель всегда можно отдать.
        StoredFileNamePattern.IsMatch(Guid.NewGuid().ToString("N") + extension).ShouldBeTrue();
    }

    [Fact(DisplayName = "Запасное расширение само проходит проверку")]
    public void Fallback_extension_is_safe()
    {
        MediaFileNames.IsSafeExtension(MediaFileNames.FallbackExtension).ShouldBeTrue();
        MediaFileNames.IsSafeExtension(null).ShouldBeFalse();
        MediaFileNames.IsSafeExtension("m4a").ShouldBeFalse(); // без точки
    }
}
