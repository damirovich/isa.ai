using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using FFMpegCore;
using FFMpegCore.Pipes;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Vision.Onnx.Video;

/// <summary>
/// Раскадровка видео внешним процессом ffmpeg (LGPL-сборка, без линковки; обёртка FFMpegCore — MIT,
/// ADR-0020): <c>-vf fps=N -f image2pipe -c:v mjpeg</c> в stdout, поток режется на кадры по мере
/// поступления и отдаётся через канал — длинное видео в памяти не собирается (ТО-мат-06).
/// Тем же ffmpeg выполняются проба видеопотока (<see cref="ProbeAsync"/>) и вырезка одного кадра по
/// времени (<see cref="ExtractFrameAsync"/>) для покадрового просмотра и снимка кадра (ADR-0028).
/// </summary>
/// <remarks>
/// Таймкод кадра — <c>индекс / fps</c>: фильтр fps выдаёт кадры с постоянным шагом. Отсутствие
/// ffmpeg — явная ошибка при первом обращении (бинарник поставляется в дистрибутиве, ТИ-004).
/// Перед раскадровкой потоки файла проверяются ffprobe (<see cref="HasVideoStreamAsync"/>): в файле без
/// видеопотока (голосовое .3gp, звук в mp4/webm) ffmpeg не создал бы ни одного выходного потока и упал бы —
/// вместо этого кадров просто нет (ADR-0026; основная проверка — в индексаторе, здесь второй рубеж).
/// Вырезка кадра и проба ограничены числом одновременных процессов
/// (<see cref="VisionOptions.EffectiveMaxConcurrentFrameExtractions"/>): лишние ждут слот не дольше таймаута
/// вырезки, затем — ошибка «сервер занят вырезкой кадров». Раскадровка предела не имеет (фоновая, по одной задаче).
/// </remarks>
public sealed class FfmpegFrameExtractor(VisionOptions options) : IFrameExtractor
{
    /// <summary>Предел длины сообщения ffprobe в тексте ошибки (статус носителя — не место для журнала ffmpeg).</summary>
    private const int MaxToolMessageLength = 1000;

    /// <summary>
    /// Предел времени на вырезку одного кадра. Один кадр 1080p при группе кадров 10 с вырезается за ~0,2 с
    /// (ADR-0028); минута — запас на медленный диск и длинную группу кадров. Дольше — значит ffmpeg завис
    /// или файл повреждён, и процесс останавливается, чтобы не держать поток запроса вечно.
    /// </summary>
    internal static readonly TimeSpan FrameExtractionTimeout = TimeSpan.FromSeconds(60);

    /// <summary>Подмена таймаута вырезки кадра для тестов (проверка остановки без минутного ожидания).</summary>
    internal TimeSpan? FrameExtractionTimeoutOverride { get; init; }

    /// <summary>
    /// Задержка внутри занятого слота перед запуском процесса — только для тестов очереди (без ffmpeg проверить,
    /// что второй вызов не стартует, пока первый держит слот).
    /// </summary>
    internal Func<CancellationToken, Task>? BeforeFrameProcessForTests { get; init; }

    // Переборка (bulkhead) на внешние процессы вырезки кадра и пробы видео (ADR-0028; ревью 25.09.2026, п. 1):
    // каждый запрос кадра с сервера — отдельный ffmpeg, декодирующий от ближайшего ключевого кадра (секунды CPU и
    // сотни МБ на 4K), и без предела один субъект с доступом к одному видео своего дела мог бы сотней параллельных
    // GET /media/frames исчерпать процессор и память узла — вместе с фоновой индексацией и расшифровкой (тот же
    // хост). Экземпляр один (singleton) → предел на процесс хоста. Раскадровка ExtractAsync сюда не входит: она
    // фоновая и идёт по одной задаче. Ожидание слота — под тем же дедлайном, что и процесс, чтобы очередь не
    // копилась бесконечно. SemaphoreSlim без AvailableWaitHandle не держит дескрипторов ОС — Dispose не нужен.
    private readonly SemaphoreSlim _frameProcesses = new(
        options.EffectiveMaxConcurrentFrameExtractions, options.EffectiveMaxConcurrentFrameExtractions);

    /// <summary>Свободных слотов на процессы вырезки/пробы (для тестов очереди).</summary>
    internal int FreeFrameProcessSlots => _frameProcesses.CurrentCount;

    /// <inheritdoc />
    public async IAsyncEnumerable<VideoFrame> ExtractAsync(
        string videoPath, FrameSamplingOptions sampling,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sampling);
        EnsureSafePath(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        var ffOptions = FfOptions();

        // Второй рубеж ADR-0026: без видеопотока — ноль кадров, а не сбой ffmpeg «нет выходных потоков».
        if (!await HasVideoStreamCoreAsync(videoPath, ffOptions, cancellationToken))
        {
            yield break;
        }

        var fps = Math.Clamp(sampling.FramesPerSecond, 0.1, 30);
        var channel = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(16) { SingleWriter = true, SingleReader = true });
        var sink = new ChannelSink(channel.Writer);

        var run = Task.Run(async () =>
        {
            try
            {
                await FFMpegArguments
                    .FromFileInput(videoPath)
                    .OutputToPipe(sink, o => o
                        .WithVideoCodec("mjpeg")
                        .ForceFormat("image2pipe")
                        .WithCustomArgument($"-vf fps={fps.ToString(CultureInfo.InvariantCulture)} -q:v 3"))
                    .CancellableThrough(cancellationToken)
                    .ProcessAsynchronously(true, ffOptions);
                channel.Writer.TryComplete();
            }
            catch (Exception exception)
            {
                channel.Writer.TryComplete(exception);
            }
        }, cancellationToken);

        var index = 0;
        await foreach (var jpeg in channel.Reader.ReadAllAsync(cancellationToken))
        {
            if (sampling.MaxFrames is { } max && index >= max)
            {
                break;
            }

            yield return new VideoFrame(index, TimeSpan.FromSeconds(index / fps), jpeg);
            index++;
        }

        await run; // пробрасывает ошибку ffmpeg (например, «бинарник не найден») наружу
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>Видеопоток — первый, кроме прикреплённого изображения (обложка звуковой записи). Частота — <c>r_frame_rate</c>
    /// ffprobe (базовая частота потока), а если она не задана — средняя <c>avg_frame_rate</c> (VFR-записи; шаг кадра
    /// тогда приблизителен, ADR-0028). Если ни одна не известна (ffprobe отдаёт <c>0/0</c>: поток без меток времени,
    /// повреждённый заголовок) или неизвестен размер кадра, результат — <see langword="null"/>: без частоты нельзя
    /// ни пронумеровать кадры, ни шагнуть на один кадр, а без размера — ни декодировать, то есть ПРИГОДНОГО для
    /// покадрового просмотра видеопотока в файле нет, хотя ffprobe его и перечислил.</para>
    /// <para>Длительность — по контейнеру (<c>format.duration</c>); если контейнер её не сообщил — по видеопотоку; если
    /// её нет нигде (незавершённая запись Matroska/WebM: заголовок без Duration, потоки без duration) —
    /// <see langword="null"/>, при этом частота и размер есть и кадры вырезаются как обычно.
    /// Размер кадра — ПОСЛЕ автоповорота по метке контейнера (телефонная съёмка с поворотом ±90°/270°: ширина и
    /// высота меняются местами) — так же кадр отдаёт <see cref="ExtractFrameAsync"/>, а раскадровка показывает его
    /// «прямо».</para>
    /// <para>Проба занимает слот того же предела одновременных процессов, что и вырезка кадра, под тем же таймаутом.</para>
    /// </remarks>
    public async Task<VideoProbe?> ProbeAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        EnsureSafePath(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        // Проба — тот же предел процессов и тот же дедлайн, что у вырезки кадра: ffprobe читает только заголовки и
        // укладывается в доли секунды, но запускается из тех же интерактивных сценариев (ADR-0028).
        var timeout = FrameExtractionTimeoutOverride ?? FrameExtractionTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await AcquireFrameProcessSlotAsync(deadline.Token, cancellationToken, timeout).ConfigureAwait(false);

        IMediaAnalysis analysis;
        try
        {
            if (BeforeFrameProcessForTests is { } hold)
            {
                await hold(deadline.Token).ConfigureAwait(false);
            }

            analysis = await ProbeCoreAsync(videoPath, FfOptions(), deadline.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Проба видео отменена.", exception, cancellationToken);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        {
            throw new InvalidOperationException(
                $"ffprobe не разобрал файл за {FormatSeconds(timeout)} с: процесс остановлен. Файл повреждён либо сервер перегружен.",
                exception);
        }
        finally
        {
            _frameProcesses.Release();
        }

        var stream = analysis.VideoStreams.Find(candidate => !IsAttachedPicture(candidate));
        if (stream is null)
        {
            return null;
        }

        var frameRate = UsableRate(stream.FrameRate) ?? UsableRate(stream.AvgFrameRate);
        if (frameRate is null || stream.Width <= 0 || stream.Height <= 0)
        {
            return null; // видеопоток есть, но кадры не пронумеровать или не декодировать — для просмотра непригоден
        }

        // Длительность: контейнер, иначе поток; ноль — «неизвестна» (незавершённая запись Matroska/WebM), а не
        // «пустая запись»: записанный ноль запер бы покадровый просмотр целиком.
        TimeSpan? duration = analysis.Duration > TimeSpan.Zero ? analysis.Duration
            : stream.Duration > TimeSpan.Zero ? stream.Duration
            : null;
        var (width, height) = IsQuarterTurn(stream.Rotation)
            ? (stream.Height, stream.Width)
            : (stream.Width, stream.Height);

        return new VideoProbe(frameRate.Value, duration, width, height);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>Точный поиск: <c>-ss</c> стоит ПЕРЕД <c>-i</c> — ffmpeg переходит к ближайшему ключевому кадру до момента
    /// и декодирует до него, отдавая ровно кадр в этот момент (проверено 25.09.2026 на mp4/mkv/mov/avi/webm/3gp, в том
    /// числе при ненулевом стартовом времени файла). Автоповорот по метке контейнера — поведение ffmpeg по умолчанию;
    /// иной обработки нет (ТЭ-007): PNG — без потерь, JPEG (<c>-q:v 2</c>) — для просмотра. Ограничение
    /// <paramref name="maxSide"/> — фильтр <c>scale</c> с сохранением пропорций: наибольшая сторона не больше предела,
    /// стороны округляются вниз до чётных (нужно кодеку); кадр меньше предела не увеличивается.</para>
    /// <para>Момент за концом записи: ffmpeg завершается кодом 0 с пустым выводом — это <see langword="null"/>, не ошибка.
    /// Файл без видеопотока: ffmpeg отказывается («Output file does not contain any stream») — это
    /// <see cref="InvalidOperationException"/>, как и сбой декодирования. Процесс, не уложившийся в
    /// <see cref="FrameExtractionTimeout"/>, останавливается, и наружу уходит <see cref="InvalidOperationException"/>
    /// с причиной, а не отмена: для вызывающего это сбой вырезки, а не отмена его задачи.</para>
    /// </remarks>
    public async Task<byte[]?> ExtractFrameAsync(
        string videoPath, TimeSpan at, FrameImageFormat format, int? maxSide = null, CancellationToken cancellationToken = default)
    {
        EnsureSafePath(videoPath);
        ArgumentOutOfRangeException.ThrowIfLessThan(at, TimeSpan.Zero);
        if (maxSide is { } side)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(side);
        }

        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        var seek = at.TotalSeconds.ToString("0.######", CultureInfo.InvariantCulture);
        var outputArguments = OutputArgumentsFor(format, maxSide);

        using var output = new MemoryStream();
        var sink = new MemorySink(output);

        // Таймаут — линкованный токен: по нему FFMpegCore останавливает процесс (регистрация в CancellableThrough), а
        // наружу уходит ошибка с причиной, не OperationCanceledException (образец — SpeechWorkerTranscriber).
        var timeout = FrameExtractionTimeoutOverride ?? FrameExtractionTimeout;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(timeout);
        await AcquireFrameProcessSlotAsync(deadline.Token, cancellationToken, timeout).ConfigureAwait(false);
        try
        {
            if (BeforeFrameProcessForTests is { } hold)
            {
                await hold(deadline.Token).ConfigureAwait(false);
            }

            // Уровень журнала ffmpeg — только ошибки: на обычном уровне ffmpeg печатает метаданные входа (название,
            // комментарии записи), а текст ошибки уходит в статус носителя и журнал — сведениям о содержимом
            // материала там не место (ТД-007).
            await FFMpegArguments
                .FromFileInput(videoPath, verifyExists: false, input => input.WithCustomArgument("-ss " + seek))
                .WithGlobalOptions(global => global.WithVerbosityLevel(FFMpegCore.Arguments.VerbosityLevel.Error))
                .OutputToPipe(sink, o => o.ForceFormat("image2pipe").WithCustomArgument(outputArguments))
                .CancellableThrough(deadline.Token)
                .ProcessAsynchronously(true, FfOptions())
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (cancellationToken.IsCancellationRequested)
        {
            // Отмена вызывающим: FFMpegCore при остановке процесса по токену бросает то TaskCanceledException, то свою
            // ошибку — наружу всегда одна отмена с токеном вызывающего.
            throw new OperationCanceledException("Вырезка кадра отменена.", exception, cancellationToken);
        }
        catch (Exception exception) when (deadline.IsCancellationRequested)
        {
            throw TimeoutError(timeout, exception);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException("ffmpeg не вырезал кадр: " + Shorten(exception.Message.Trim()), exception);
        }
        finally
        {
            _frameProcesses.Release();
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (deadline.IsCancellationRequested)
        {
            // Процесс остановлен по таймауту, но обёртка не бросила: вывод либо пуст, либо оборван на середине кадра —
            // отдавать его как «кадра нет» или как целый кадр нельзя.
            throw TimeoutError(timeout, innerException: null);
        }

        // Момент за концом записи: ffmpeg завершился без ошибки, но кадра не выдал (проверено на всех контейнерах пакета).
        return output.Length == 0 ? null : output.ToArray();
    }

    /// <summary>
    /// Занимает слот переборки процессов (<see cref="_frameProcesses"/>). Ожидание — под дедлайном вызова: если за
    /// таймаут слот не освободился — сервер занят вырезкой кадров, и наружу уходит ошибка с причиной (как таймаут
    /// процесса), а не бесконечная очередь; отмена вызывающим — отмена.
    /// </summary>
    private async Task AcquireFrameProcessSlotAsync(CancellationToken deadlineToken, CancellationToken cancellationToken, TimeSpan timeout)
    {
        try
        {
            await _frameProcesses.WaitAsync(deadlineToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Вырезка кадра отменена.", exception, cancellationToken);
        }
        catch (OperationCanceledException exception)
        {
            throw new InvalidOperationException(
                $"Сервер занят вырезкой кадров: свободного процесса ffmpeg не дождались за {FormatSeconds(timeout)} с "
                + $"(одновременно — не больше {options.EffectiveMaxConcurrentFrameExtractions.ToString(CultureInfo.InvariantCulture)}, "
                + $"ключ {VisionOptions.MaxConcurrentFrameExtractionsKey}).",
                exception);
        }
    }

    private static InvalidOperationException TimeoutError(TimeSpan timeout, Exception? innerException) =>
        new(
            $"ffmpeg не вырезал кадр за {FormatSeconds(timeout)} с: процесс остановлен. Файл повреждён либо сервер перегружен.",
            innerException);

    private static string FormatSeconds(TimeSpan value) =>
        value.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);

    /// <inheritdoc />
    public Task<bool> HasVideoStreamAsync(string videoPath, CancellationToken cancellationToken = default)
    {
        EnsureSafePath(videoPath);
        if (!File.Exists(videoPath))
        {
            throw new FileNotFoundException($"Видеофайл не найден: {videoPath}", videoPath);
        }

        return HasVideoStreamCoreAsync(videoPath, FfOptions(), cancellationToken);
    }

    /// <summary>
    /// Аргументы вывода вырезки кадра: один кадр (<c>-frames:v 1</c>), без звука/субтитров/данных, кодек по формату и —
    /// при пределе стороны — фильтр масштаба. Отдельным методом ради проверки без ffmpeg.
    /// </summary>
    /// <param name="format">PNG (без потерь) или JPEG (<c>-q:v 2</c> — почти без потерь, для просмотра).</param>
    /// <param name="maxSide">Предел наибольшей стороны или <see langword="null"/> — исходный размер.</param>
    internal static string OutputArgumentsFor(FrameImageFormat format, int? maxSide)
    {
        // JPEG: -color_range pc закрепляет полный диапазон (JPEG иного не знает; при наличии кадра ffmpeg выбирает его и
        // сам — вывод побайтно тот же). -strict unofficial нужен ТОЛЬКО для момента за концом записи: кадров нет, граф
        // фильтров не настраивается, и ffmpeg (≥ 7) открывает кодер «вслепую» по формату ВХОДА — yuv420p ограниченного
        // диапазона, который mjpeg без этого ключа отвергает, и вместо пустого вывода с кодом 0 был бы сбой. Формат
        // (4:2:0/4:4:4) не закрепляется: пин прореживал бы цветность у источников 4:4:4 (проверено 25.09.2026 на ffmpeg 9
        // поставки: все контейнеры пакета, с масштабом и без, — вывод тот же, за концом — пусто).
        var codec = format switch
        {
            FrameImageFormat.Png => "-c:v png",
            FrameImageFormat.Jpeg => "-c:v mjpeg -q:v 2 -color_range pc -strict unofficial",
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Неизвестный формат кадра."),
        };

        // scale с min(): кадр крупнее предела вписывается в квадрат predel×predel с сохранением пропорций
        // (force_original_aspect_ratio=decrease — ограничивается НАИБОЛЬШАЯ сторона, а не только ширина), кадр
        // меньше — не увеличивается; стороны — чётные (force_divisible_by=2). Апострофы — кавычки синтаксиса
        // фильтров ffmpeg (запятая внутри min() иначе разделила бы фильтры); до ffmpeg они доходят как есть и в
        // Windows, и в Linux — командную строку разбирает не оболочка.
        var scale = maxSide is { } side
            ? $" -vf scale=w='min({side.ToString(CultureInfo.InvariantCulture)},iw)':h='min({side.ToString(CultureInfo.InvariantCulture)},ih)'"
              + ":force_original_aspect_ratio=decrease:force_divisible_by=2"
            : string.Empty;

        return $"-an -sn -dn -frames:v 1 {codec}{scale}";
    }

    /// <summary>
    /// Отказ ДО запуска внешнего процесса, если путь разорвал бы его командную строку: FFMpegCore передаёт путь
    /// текстом в кавычках без экранирования, и кавычка в пути или «\» в его конце (экранирует закрывающую
    /// кавычку) превращаются в лишние аргументы ffmpeg/ffprobe — посторонний вход или выход. Имена временных
    /// копий конвейер строит сам (GUID и отфильтрованное расширение), так что здесь это страховка.
    /// </summary>
    private static void EnsureSafePath(string videoPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(videoPath);
        if (videoPath.Contains('"', StringComparison.Ordinal) || videoPath.EndsWith('\\'))
        {
            throw new ArgumentException(
                "Путь к видеофайлу содержит кавычку или оканчивается обратной косой чертой: внешний процесс ffmpeg "
                + "получил бы лишние аргументы. Файл не обрабатывается.",
                nameof(videoPath));
        }
    }

    /// <summary>
    /// Проба потоков ffprobe. Видеопоток — любой, кроме прикреплённого изображения (обложка звуковой записи
    /// видеопотоком для ffprobe является, но картинки во времени у неё нет).
    /// </summary>
    private static async Task<bool> HasVideoStreamCoreAsync(string videoPath, FFOptions ffOptions, CancellationToken cancellationToken)
    {
        var analysis = await ProbeCoreAsync(videoPath, ffOptions, cancellationToken).ConfigureAwait(false);
        return analysis.VideoStreams.Exists(stream => !IsAttachedPicture(stream));
    }

    /// <summary>
    /// Общий вызов ffprobe для пробы потоков и пробы видео: ошибка инструмента (не найден, файл не разобран) —
    /// <see cref="InvalidOperationException"/> с укороченным текстом ffprobe; отмена — как есть.
    /// </summary>
    private static async Task<IMediaAnalysis> ProbeCoreAsync(string videoPath, FFOptions ffOptions, CancellationToken cancellationToken)
    {
        try
        {
            return await FFProbe.AnalyseAsync(videoPath, ffOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("ffprobe не разобрал файл: " + Shorten(exception.Message.Trim()), exception);
        }
    }

    private static bool IsAttachedPicture(VideoStream stream) =>
        stream.Disposition is { } disposition
        && disposition.TryGetValue("attached_pic", out var attached)
        && attached;

    // Частота из ffprobe пригодна, если это конечное положительное число («0/0» FFMpegCore отдаёт как 0 или NaN).
    private static double? UsableRate(double rate) =>
        double.IsFinite(rate) && rate > 0 ? rate : null;

    // Метка поворота ±90°/±270° (телефонная съёмка): после автоповорота ширина и высота меняются местами.
    private static bool IsQuarterTurn(int rotation) =>
        Math.Abs(rotation) % 180 == 90;

    private FFOptions FfOptions()
    {
        var ffOptions = new FFOptions();
        if (!string.IsNullOrWhiteSpace(options.FfmpegFolder))
        {
            ffOptions.BinaryFolder = options.FfmpegFolder;
        }

        return ffOptions;
    }

    // Сообщение ffprobe бывает многострочным и длинным; статусу носителя нужна суть, а не весь журнал.
    private static string Shorten(string message) =>
        message.Length <= MaxToolMessageLength ? message : message[..MaxToolMessageLength] + "…";

    // Приёмник stdout ffmpeg: читает поток и режет на кадры прямо по мере поступления.
    private sealed class ChannelSink(ChannelWriter<byte[]> writer) : IPipeSink
    {
        public string GetFormat() => "image2pipe";

        public async Task ReadAsync(Stream inputStream, CancellationToken cancellationToken)
        {
            using var splitter = new MjpegStreamSplitter();
            var buffer = new byte[64 * 1024];
            int read;
            while ((read = await inputStream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                foreach (var frame in splitter.Push(buffer.AsSpan(0, read)))
                {
                    await writer.WriteAsync(frame, cancellationToken);
                }
            }
        }
    }

    // Приёмник stdout ffmpeg для ОДНОГО кадра: весь вывод — в память (один кадр PNG 1080p — единицы мегабайт).
    private sealed class MemorySink(MemoryStream destination) : IPipeSink
    {
        public string GetFormat() => "image2pipe";

        public Task ReadAsync(Stream inputStream, CancellationToken cancellationToken) =>
            inputStream.CopyToAsync(destination, cancellationToken);
    }
}
