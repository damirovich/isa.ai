using System;
using System.Buffers.Binary;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ISC.AI.Evals.Speech;

/// <summary>
/// Длительность звука записи — знаменатель коэффициента реального времени (RTF). Измеряется по самому файлу —
/// и для готового выхода сторонних моделей, где распознаватель не вызывается. Порт <c>IAudioTranscriber</c>
/// тоже сообщает длительность записи (<c>AudioTranscription.DurationMs</c>); прогон берёт её, когда измерить
/// файл не удалось.
/// </summary>
public interface IAudioDurationProbe
{
    /// <summary>Длительность звука файла или <see langword="null"/>, если определить не удалось.</summary>
    /// <param name="path">Полный путь к записи.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken = default);
}

/// <summary>
/// Длительность записи: WAV — по заголовку (без внешних программ), остальные форматы (mp3, m4a, ogg/opus,
/// amr, видео) — локальным <c>ffprobe</c> из офлайн-поставки ffmpeg (ТИ-004; тот же каталог, что у ключа
/// <c>Speech:Ffmpeg:Folder</c>). Сеть не используется.
/// </summary>
/// <remarks>
/// Неудача — не ошибка прогона: без измерения прогон берёт длительность, сообщённую распознавателем, а если
/// нет и её — конец последнего фрагмента речи (оценка снизу, RTF помечается как приблизительный). Поэтому
/// отсутствие ffprobe даёт <see langword="null"/>, а не исключение.
/// </remarks>
/// <param name="ffprobeFolder">Каталог ffprobe; <see langword="null"/> — искать в PATH.</param>
/// <param name="useFfprobe"><see langword="false"/> — только WAV-заголовок (для тестов и машин без ffmpeg).</param>
public sealed class AudioDurationProbe(string? ffprobeFolder = null, bool useFfprobe = true) : IAudioDurationProbe
{
    // Заголовок WAV с «fmt » и началом «data» почти всегда в первых килобайтах; 64 КБ — с запасом на LIST.
    private const int WavHeaderBytes = 64 * 1024;

    private static readonly TimeSpan FfprobeTimeout = TimeSpan.FromMinutes(2);

    /// <inheritdoc />
    public async Task<TimeSpan?> GetDurationAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        if (string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase))
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize: 4096, useAsync: true);
            await using (stream.ConfigureAwait(false))
            {
                var header = new byte[(int)Math.Min(WavHeaderBytes, stream.Length)];
                var read = await stream.ReadAtLeastAsync(header, header.Length, throwOnEndOfStream: false, cancellationToken).ConfigureAwait(false);
                if (TryReadWavDuration(header.AsSpan(0, read), stream.Length, out var wavDuration))
                {
                    return wavDuration;
                }
            }
        }

        return useFfprobe ? await RunFfprobeAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }

    /// <summary>
    /// Длительность PCM-звука по заголовку RIFF/WAVE: размер чанка <c>data</c> / байт в секунду из <c>fmt </c>.
    /// Размер <c>data</c> 0 или <c>0xFFFFFFFF</c> (так пишут потоковые кодировщики) либо больше файла —
    /// берётся остаток файла.
    /// </summary>
    /// <param name="header">Начало файла (заголовок и начало данных).</param>
    /// <param name="fileLength">Полный размер файла, байт.</param>
    /// <param name="duration">Длительность при успехе.</param>
    public static bool TryReadWavDuration(ReadOnlySpan<byte> header, long fileLength, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (header.Length < 12 || !header[..4].SequenceEqual("RIFF"u8) || !header.Slice(8, 4).SequenceEqual("WAVE"u8))
        {
            return false;
        }

        long position = 12;
        uint byteRate = 0;
        while (position + 8 <= header.Length)
        {
            var chunk = header.Slice((int)position);
            var id = chunk[..4];
            var size = BinaryPrimitives.ReadUInt32LittleEndian(chunk.Slice(4, 4));
            var body = position + 8;

            if (id.SequenceEqual("fmt "u8))
            {
                if (body + 16 > header.Length)
                {
                    return false;
                }

                byteRate = BinaryPrimitives.ReadUInt32LittleEndian(header.Slice((int)body + 8, 4));
            }
            else if (id.SequenceEqual("data"u8))
            {
                if (byteRate == 0)
                {
                    return false;
                }

                var available = fileLength - body;
                var dataSize = size == 0 || size == uint.MaxValue || size > available ? available : size;
                if (dataSize < 0)
                {
                    return false;
                }

                duration = TimeSpan.FromSeconds((double)dataSize / byteRate);
                return true;
            }

            // Чанки выровнены на чётную границу.
            position = body + size + (size & 1);
        }

        return false;
    }

    /// <summary>
    /// Разбирает вывод <c>ffprobe -show_entries format=duration -of default=noprint_wrappers=1:nokey=1</c>:
    /// секунды с точкой («83.450000»). <c>N/A</c> и мусор — неудача.
    /// </summary>
    /// <param name="output">Стандартный вывод ffprobe.</param>
    /// <param name="duration">Длительность при успехе.</param>
    public static bool TryParseFfprobeDuration(string? output, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        var text = output?.Trim();
        if (string.IsNullOrEmpty(text)
            || !double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds)
            || !double.IsFinite(seconds)
            || seconds < 0)
        {
            return false;
        }

        duration = TimeSpan.FromSeconds(seconds);
        return true;
    }

    private async Task<TimeSpan?> RunFfprobeAsync(string path, CancellationToken cancellationToken)
    {
        var executable = string.IsNullOrWhiteSpace(ffprobeFolder)
            ? "ffprobe"
            : Path.Combine(ffprobeFolder, OperatingSystem.IsWindows() ? "ffprobe.exe" : "ffprobe");

        var startInfo = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        // Аргументы — списком, путь не склеивается в командную строку (пробелы, кавычки в имени файла).
        foreach (var argument in new[] { "-v", "error", "-show_entries", "format=duration", "-of", "default=noprint_wrappers=1:nokey=1", path })
        {
            startInfo.ArgumentList.Add(argument);
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return null;
            }
        }
        catch (Win32Exception)
        {
            // ffprobe не найден — длительность неизвестна, прогон возьмёт её у распознавателя или по фрагментам.
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(FfprobeTimeout);
        try
        {
            var standardOutput = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var standardError = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            var output = await standardOutput.ConfigureAwait(false);
            await standardError.ConfigureAwait(false);

            return process.ExitCode == 0 && TryParseFfprobeDuration(output, out var duration) ? duration : null;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // Зависший ffprobe (битый файл) — длительность неизвестна, прогон продолжается.
            return null;
        }
        finally
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // Процесс завершился между проверкой и остановкой — останавливать нечего.
            }
        }
    }
}
