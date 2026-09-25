using System;
using System.IO;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Имена файлов носителей на диске: безопасное расширение для хранилища и временных копий конвейеров.
/// </summary>
/// <remarks>
/// ПОЧЕМУ ФИЛЬТР. Расширение берётся из имени файла, пришедшего из браузера, то есть из изъятого устройства,
/// и никем не проверяется. Внешние процессы ffmpeg/ffprobe (обёртка FFMpegCore) получают путь ТЕКСТОМ
/// командной строки в кавычках, без экранирования. Кавычка в расширении или обратная косая черта в его конце
/// разрывают эти кавычки: на Linux-сервере (поставка linux-x64) это лишние аргументы ffmpeg, вплоть до
/// постороннего входа или выхода. Поэтому расширение остаётся, только если это 2–5 латинских букв или цифр
/// (тот же формат, что принимает раздача файлов <c>MediaFileEndpoints</c>, иначе сохранённый файл нельзя было бы
/// отдать); всё прочее заменяется на <see cref="FallbackExtension"/>. Формат ffmpeg определяет по содержимому,
/// а не по расширению, так что обработка от замены не страдает. Фильтр один на приём и на временные копии:
/// носители, сохранённые до него с «плохим» расширением, закрыты тем же правилом.
/// </remarks>
public static class MediaFileNames
{
    /// <summary>Расширение для файла, чьё исходное расширение отсутствует или недопустимо.</summary>
    public const string FallbackExtension = ".bin";

    /// <summary>Наименьшая длина допустимого расширения без точки.</summary>
    private const int MinExtensionLength = 2;

    /// <summary>Наибольшая длина допустимого расширения без точки.</summary>
    private const int MaxExtensionLength = 5;

    /// <summary>
    /// Расширение имени <paramref name="fileName"/> с точкой, если оно из 2–5 латинских букв и цифр; иначе
    /// <see cref="FallbackExtension"/>. Регистр сохраняется.
    /// </summary>
    public static string SafeExtension(string? fileName)
    {
        var extension = Path.GetExtension(fileName ?? string.Empty);
        return IsSafeExtension(extension) ? extension : FallbackExtension;
    }

    /// <summary>Расширение (с точкой) состоит из 2–5 латинских букв и цифр — и ничего больше.</summary>
    public static bool IsSafeExtension(string? extension)
    {
        if (extension is null
            || extension.Length < MinExtensionLength + 1
            || extension.Length > MaxExtensionLength + 1
            || extension[0] != '.')
        {
            return false;
        }

        foreach (var symbol in extension.AsSpan(1))
        {
            if (!char.IsAsciiLetterOrDigit(symbol))
            {
                return false;
            }
        }

        return true;
    }
}
