using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using Shouldly;

namespace ISC.AI.IntegrationTests.Vision;

/// <summary>
/// Обвязка тестов видео (категория <c>Video</c>): поставочный ffmpeg и генерация синтетических клипов им же.
/// В репозитории нет ни одного медиафайла, а реальные съёмки людей в тесты не попадают (ТО-прог-13): клипы —
/// <c>testsrc</c>/<c>testsrc2</c> ffmpeg, создаются во временном каталоге на время прогона.
/// </summary>
/// <remarks>
/// Требуется поставка ffmpeg: <c>deploy/offline/ffmpeg/win-x64</c> (скрипт <c>deploy/offline/export-ffmpeg.ps1</c>,
/// ТИ-004, ADR-0020). Без неё тесты падают с инструкцией — намеренно, вместо тихого пропуска: «видео не
/// проверено» должно быть видно. Прогон без поставки: <c>dotnet test --filter "Category!=Video"</c>.
/// В LGPL-сборке нет libx264/libx265: H.264 кодируется <c>h264_mf</c> (Media Foundation, Windows), MPEG-4 part 2 —
/// <c>mpeg4</c>, H.263 — <c>h263</c>, VP9 — <c>libvpx-vp9</c>.
/// </remarks>
internal static class VideoTestEnvironment
{
    /// <summary>Папка поставки ffmpeg; её отсутствие — явная ошибка с инструкцией, а не тихий пропуск.</summary>
    public static string LocateFfmpegFolder()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "ISC.AI.slnx")))
        {
            dir = dir.Parent;
        }

        var folder = Path.Combine(dir?.FullName ?? ".", "deploy", "offline", "ffmpeg", "win-x64");
        if (!File.Exists(Path.Combine(folder, "ffmpeg.exe")))
        {
            throw new InvalidOperationException(
                $"Поставка ffmpeg не найдена: {folder}. Выполните deploy/offline/export-ffmpeg.ps1 "
                + "(LGPL-сборка с пином SHA-256, ADR-0020/ТИ-004) либо исключите категорию: "
                + "dotnet test --filter \"Category!=Video\".");
        }

        return folder;
    }

    /// <summary>Новый временный каталог прогона (клипы, эталонные кадры).</summary>
    public static string CreateWorkDir()
    {
        var workDir = Path.Combine(Path.GetTempPath(), "iscai-video-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDir);
        return workDir;
    }

    /// <summary>Удаляет временный каталог; сбой удаления — не повод валить прогон.</summary>
    public static void TryDeleteWorkDir(string workDir)
    {
        try
        {
            Directory.Delete(workDir, recursive: true);
        }
        catch (IOException)
        {
            // Временный каталог — не повод валить прогон.
        }
        catch (UnauthorizedAccessException)
        {
            // То же: файл ещё держит завершающийся процесс.
        }
    }

    /// <summary>
    /// Запускает поставочный ffmpeg с аргументами <paramref name="arguments"/> (без баннера, журнал — только ошибки)
    /// и требует код 0; текст stderr — в сообщении об ошибке.
    /// </summary>
    public static async Task RunFfmpegAsync(string ffmpegFolder, string arguments)
    {
        var info = new ProcessStartInfo(Path.Combine(ffmpegFolder, "ffmpeg.exe"), "-hide_banner -loglevel error " + arguments)
        {
            RedirectStandardError = true,
            UseShellExecute = false,
        };

        using var process = Process.Start(info) ?? throw new InvalidOperationException("Не удалось запустить ffmpeg.");
        var error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        process.ExitCode.ShouldBe(0, $"ffmpeg {arguments}{Environment.NewLine}{error}");
    }
}
