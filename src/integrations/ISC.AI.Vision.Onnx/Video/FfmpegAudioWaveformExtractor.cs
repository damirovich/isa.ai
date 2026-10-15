using System.Globalization;
using FFMpegCore;
using FFMpegCore.Pipes;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Vision.Onnx.Video;

/// <summary>
/// Волна аудиозаписи внешним процессом ffmpeg (ADR-0047; LGPL-сборка без линковки, обёртка FFMpegCore — MIT,
/// ADR-0020): звук ОРИГИНАЛА декодируется в моно 8 кГц 16 бит (<c>-ac 1 -ar 8000 -f s16le</c>) прямо в stdout, и
/// <see cref="AudioWaveformBuilder"/> по мере поступления берёт наибольший уровень в каждом окне 20 мс. Запись в
/// памяти целиком не собирается (часовая — 180 000 чисел вместо 57 МБ звука); файл не меняется (ТЭ-007).
/// </summary>
/// <remarks>
/// <para>Одновременных процессов волны — не больше <see cref="VisionOptions.EffectiveMaxConcurrentFrameExtractions"/>
/// (своя переборка, не общая с вырезкой кадра: долгое декодирование часовой записи не должно занимать слоты покадрового
/// листания). Лишние ждут слот под тем же дедлайном, что и процесс, затем — ошибка «сервер занят». Прогон — не дольше
/// <see cref="WaveformTimeout"/>: декодирование MP3/AAC в 8 кГц идёт в сотни раз быстрее реального времени, и десяти
/// минут хватает на записи в десятки часов; дольше — ffmpeg завис или файл повреждён.</para>
/// <para>Уровень журнала ffmpeg — только ошибки: на обычном уровне он печатает метаданные входа (название, комментарии
/// записи), а текст ошибки уходит в журнал диагностики — сведениям о содержимом материала там не место (ТД-007).</para>
/// </remarks>
public sealed class FfmpegAudioWaveformExtractor(VisionOptions options) : IAudioWaveformExtractor, IDisposable
{
    /// <summary>Предел времени на построение волны одной записи (см. remarks).</summary>
    internal static readonly TimeSpan WaveformTimeout = TimeSpan.FromMinutes(10);

    /// <summary>Аргументы вывода: только звук, моно, 8 кГц, 16 бит со знаком little-endian — то, что ждёт построитель.</summary>
    internal static readonly string OutputArguments =
        "-vn -sn -dn -ac 1 -ar " + AudioWaveform.SampleRate.ToString(CultureInfo.InvariantCulture) + " -c:a pcm_s16le";

    /// <summary>Подмена таймаута для тестов (проверка остановки без десятиминутного ожидания).</summary>
    internal TimeSpan? TimeoutOverride { get; init; }

    // Переборка процессов волны (см. remarks); экземпляр один (singleton) → предел на процесс хоста. Освобождается
    // контейнером при остановке хоста (Dispose).
    private readonly SemaphoreSlim _processes = new(
        options.EffectiveMaxConcurrentFrameExtractions, options.EffectiveMaxConcurrentFrameExtractions);

    /// <inheritdoc />
    public async Task<AudioWaveform> ExtractAsync(string audioPath, CancellationToken cancellationToken = default)
    {
        FfmpegFrameExtractor.EnsureSafePath(audioPath);
        if (!File.Exists(audioPath))
        {
            throw new FileNotFoundException($"Файл записи не найден: {audioPath}", audioPath);
        }

        var timeout = TimeoutOverride ?? WaveformTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await AcquireSlotAsync(timeout, deadline.Token, cancellationToken).ConfigureAwait(false);

        var builder = new AudioWaveformBuilder();
        try
        {
            await FFMpegArguments
                .FromFileInput(audioPath, verifyExists: false)
                .WithGlobalOptions(global => global.WithVerbosityLevel(FFMpegCore.Arguments.VerbosityLevel.Error))
                .OutputToPipe(new BuilderSink(builder), o => o.ForceFormat(BuilderSink.Format).WithCustomArgument(OutputArguments))
                .CancellableThrough(deadline.Token)
                .ProcessAsynchronously(true, FfmpegFrameExtractor.CreateFfOptions(options))
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена вызывающим (уход клиента): FFMpegCore бросает то TaskCanceledException, то свою ошибку — наружу одна отмена.
            throw new OperationCanceledException("Построение волны отменено.", exception, cancellationToken);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        {
            throw TimeoutError(timeout, exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "ffmpeg не построил волну записи: " + FfmpegFrameExtractor.Shorten(exception.Message.Trim()), exception);
        }
        finally
        {
            _processes.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
        {
            // Процесс остановлен по таймауту, но обёртка не бросила: звук прочитан не до конца — такая волна неполна.
            throw TimeoutError(timeout, innerException: null);
        }

        return builder.Build();
    }

    /// <inheritdoc />
    public void Dispose() => _processes.Dispose();

    private async Task AcquireSlotAsync(TimeSpan timeout, CancellationToken deadlineToken, CancellationToken cancellationToken)
    {
        try
        {
            await _processes.WaitAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Построение волны отменено.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"Сервер занят построением волн записей: свободного процесса ffmpeg не дождались за {FormatSeconds(timeout)} с "
                + $"(одновременно — не больше {options.EffectiveMaxConcurrentFrameExtractions.ToString(CultureInfo.InvariantCulture)}, "
                + $"ключ {VisionOptions.MaxConcurrentFrameExtractionsKey}).",
                exception);
        }
    }

    private static InvalidOperationException TimeoutError(TimeSpan timeout, Exception? innerException) =>
        new($"ffmpeg не построил волну записи за {FormatSeconds(timeout)} с: процесс остановлен. Файл повреждён либо сервер перегружен.",
            innerException);

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    // Приёмник stdout ffmpeg: звук идёт в построитель кусками по мере поступления.
    private sealed class BuilderSink(AudioWaveformBuilder builder) : IPipeSink
    {
        public const string Format = "s16le";

        public string GetFormat() => Format;

        public async Task ReadAsync(Stream inputStream, CancellationToken cancellationToken)
        {
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await inputStream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
            {
                builder.Push(buffer.AsSpan(0, read));
            }
        }
    }
}
