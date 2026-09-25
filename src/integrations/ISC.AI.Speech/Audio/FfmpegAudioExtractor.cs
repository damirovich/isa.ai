using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using FFMpegCore;

namespace ISC.AI.Speech.Audio;

/// <summary>Итог извлечения звука: подготовленный WAV и его длительность.</summary>
/// <param name="Duration">
/// Длительность WAV по объёму данных — ровно то, что получит процесс-распознаватель (с тишиной, которой ffmpeg
/// добил начало и разрывы меток времени). По ней считается таймаут прогона.
/// </param>
public sealed record ExtractedAudio(TimeSpan Duration);

/// <summary>
/// Приведение звука любого файла (аудио или видео) к единому формату модели — WAV 16 кГц, моно, PCM 16 бит —
/// внешним процессом ffmpeg (LGPL-сборка, без линковки; обёртка FFMpegCore — MIT; ADR-0020, ADR-0026).
/// </summary>
/// <remarks>
/// <para>Контейнеры и кодеки голосовых сообщений и записей (mp3, m4a/aac, ogg/opus, amr, 3gp, webm, flac, wav)
/// и звуковые дорожки видео разбирает ffmpeg; сама утилита распознавания знает только один формат.
/// Из нескольких звуковых дорожек ffmpeg берёт основную (с наибольшим числом каналов), каналы сводятся в
/// моно. Отсутствие ffmpeg — явная ошибка с указанием ключа конфигурации (бинарник — в офлайн-поставке,
/// ТИ-004).</para>
/// <para>ПРЕДЕЛ ДЛИТЕЛЬНОСТИ (ADR-0026). Запись длиннее предела (<see cref="SpeechOptions.MaxDuration"/>) —
/// явный отказ ДО запуска ffmpeg и распознавателя: иначе многочасовой прогон занимает единственную фоновую
/// очередь, WAV растёт до гигабайт, а за 37,28 ч индекс отсчёта детектора речи (int32) переполняется и прогон
/// падает в самом конце. Длительность из заголовка файла ненадёжна (нет её, занижена, разрывы меток времени,
/// которые фильтр ниже добивает тишиной), поэтому ffmpeg дополнительно ограничен ключом <c>-t</c> (предел +
/// 1 с), а длина готового WAV сверяется с пределом: вышла за предел — тот же отказ, а не молча обрезанный
/// хвост записи.</para>
/// <para>КОМАНДНАЯ СТРОКА. FFMpegCore собирает аргументы ffprobe/ffmpeg ТЕКСТОМ без экранирования (путь —
/// в кавычках). Кавычка в пути или обратная косая черта в конце (она экранирует закрывающую кавычку) разбили
/// бы строку на посторонние аргументы — такие пути отвергаются до запуска процессов (на Linux кавычка в имени
/// файла допустима, а расширение временной копии берётся из имени файла от браузера).</para>
/// </remarks>
public sealed class FfmpegAudioExtractor
{
    /// <summary>Байт в секунде WAV формата модели: 16 000 отсчётов × 2 байта (моно, PCM 16 бит).</summary>
    public const int WaveBytesPerSecond = 16000 * 2;

    // -vn/-sn/-dn — только звук; -map_metadata -1 — теги исходника (имена, координаты) во временный
    // файл не переносятся; aresample=async=1:first_pts=0 — если звук в контейнере начинается не с нуля
    // (задержка дорожки видео, «разгон» кодека), начало добивается тишиной: отсчёт WAV совпадает со шкалой
    // проигрывателя, и переход к месту записи по таймкоду фрагмента попадает туда, где слово звучит;
    // -ac 1 -ar 16000 pcm_s16le — формат детектора речи и модели. Ключ -t (жёсткий потолок) — в ExtractAsync.
    private const string OutputArguments =
        "-vn -sn -dn -map_metadata -1 -af aresample=async=1:first_pts=0 -ac 1 -ar 16000 -c:a pcm_s16le -f wav";

    // Запас потолка -t над пределом: вывод длиннее предела отличим от записи ровно по пределу, даже если
    // ffmpeg режет по границе кадра, а не по отсчёту.
    private static readonly TimeSpan OutputCapMargin = TimeSpan.FromSeconds(1);

    private const int MaxToolMessageLength = 1000;

    private readonly string? _ffmpegFolder;
    private readonly TimeSpan _maxDuration;

    /// <summary>Создаёт извлекатель; процессы и диск на этом шаге не трогаются.</summary>
    /// <param name="ffmpegFolder">Каталог ffmpeg/ffprobe; пусто — искать в PATH.</param>
    /// <param name="maxDuration">
    /// Предел длительности записи (<see cref="SpeechOptions.MaxDuration"/>); больше жёсткого предела
    /// <see cref="SpeechOptions.MaxAllowedDurationHours"/> не бывает.
    /// </param>
    public FfmpegAudioExtractor(string? ffmpegFolder, TimeSpan maxDuration)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDuration, TimeSpan.Zero);
        _ffmpegFolder = ffmpegFolder;
        _maxDuration = TimeSpan.FromTicks(Math.Min(maxDuration.Ticks, TimeSpan.FromHours(SpeechOptions.MaxAllowedDurationHours).Ticks));
    }

    /// <summary>
    /// Извлекает звук из <paramref name="sourcePath"/> в <paramref name="wavPath"/>. Возвращает
    /// <see langword="null"/>, если звуковой дорожки в файле нет (видео камер наблюдения, «немые» ролики):
    /// тогда и расшифровывать нечего — это не ошибка.
    /// </summary>
    /// <remarks>При отказе файл <paramref name="wavPath"/> может остаться — его удаляет вызывающий (он же создал каталог).</remarks>
    /// <exception cref="InvalidOperationException">
    /// Путь небезопасен для командной строки ffmpeg; запись длиннее предела; ffmpeg/ffprobe не найден или не смог
    /// разобрать файл.
    /// </exception>
    /// <exception cref="OperationCanceledException">Отмена.</exception>
    public async Task<ExtractedAudio?> ExtractAsync(string sourcePath, string wavPath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        ArgumentException.ThrowIfNullOrWhiteSpace(wavPath);
        EnsureSafeForCommandLine(sourcePath);
        EnsureSafeForCommandLine(wavPath);

        var options = new FFOptions();
        if (!string.IsNullOrWhiteSpace(_ffmpegFolder))
        {
            options.BinaryFolder = _ffmpegFolder;
        }

        IMediaAnalysis analysis;
        try
        {
            analysis = await FFProbe.AnalyseAsync(sourcePath, options, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(Describe("ffprobe не разобрал файл", exception), exception);
        }

        if (analysis.AudioStreams.Count == 0)
        {
            return null;
        }

        EnsureWithinLimit(ProbedDuration(analysis.Duration, analysis.AudioStreams.Select(stream => stream.Duration)), _maxDuration);

        var cap = (_maxDuration + OutputCapMargin).TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        try
        {
            // Уровень журнала ffmpeg — только ошибки: на обычном уровне ffmpeg печатает метаданные входа
            // (название, комментарии записи), а текст ошибки уходит в статус носителя и журнал — сведениям
            // о содержимом материала там не место (ТД-007).
            await FFMpegArguments
                .FromFileInput(sourcePath)
                .WithGlobalOptions(global => global.WithVerbosityLevel(FFMpegCore.Arguments.VerbosityLevel.Error))
                .OutputToFile(wavPath, overwrite: true, output => output.WithCustomArgument($"{OutputArguments} -t {cap}"))
                .CancellableThrough(cancellationToken)
                .ProcessAsynchronously(true, options)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException(Describe("ffmpeg не извлёк звук", exception), exception);
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (!File.Exists(wavPath))
        {
            throw new InvalidOperationException("ffmpeg завершился без ошибки, но файл звука не создан — проверьте сборку ffmpeg.");
        }

        var extracted = TimeSpan.FromSeconds((double)ReadWaveDataBytes(wavPath) / WaveBytesPerSecond);
        if (extracted > _maxDuration)
        {
            // Заголовок файла длительность не сообщил или занизил (разрывы меток времени добиты тишиной), а звук
            // длиннее предела: расшифровать только начало и молча потерять хвост — хуже явного отказа.
            throw new InvalidOperationException(
                $"Запись длиннее {FormatDuration(_maxDuration)}: заголовок файла длительность не указал или занизил, "
                + $"а звук после приведения к формату модели вышел за предел. {SplitAdvice(_maxDuration)}");
        }

        return new ExtractedAudio(extracted);
    }

    /// <summary>
    /// Длительность записи по данным ffprobe для проверки предела: НАИБОЛЬШАЯ из длительности контейнера и
    /// звуковых дорожек (у сбойных или собранных вручную файлов они расходятся — берётся худший случай).
    /// <see langword="null"/> — ffprobe длительность не сообщил (тогда предел держит потолок <c>-t</c>).
    /// </summary>
    public static TimeSpan? ProbedDuration(TimeSpan container, IEnumerable<TimeSpan> audioStreams)
    {
        ArgumentNullException.ThrowIfNull(audioStreams);
        var longest = audioStreams.Aggregate(container, (max, stream) => stream > max ? stream : max);
        return longest > TimeSpan.Zero ? longest : null;
    }

    /// <summary>
    /// Отказ, если запись длиннее предела: расшифровка такой записи за один прогон не выполняется (ADR-0026).
    /// Неизвестная длительность (<see langword="null"/>) пропускается — её ограничивает потолок <c>-t</c> и
    /// сверка длины готового WAV. В тексте — только длительности, без сведений о содержимом (ТД-007).
    /// </summary>
    /// <exception cref="InvalidOperationException">Запись длиннее предела.</exception>
    public static void EnsureWithinLimit(TimeSpan? duration, TimeSpan limit)
    {
        if (duration is { } value && value > limit)
        {
            throw new InvalidOperationException(
                $"Запись длиннее {FormatDuration(limit)} (по данным ffprobe — {FormatDuration(value)}). {SplitAdvice(limit)}");
        }
    }

    /// <summary>
    /// Отказ для пути, который FFMpegCore не может безопасно передать ffprobe/ffmpeg: кавычка внутри или
    /// обратная косая черта в конце разбили бы командную строку на посторонние аргументы (посторонние опции,
    /// лишний вход или выход ffmpeg). Проверяется ДО запуска любого процесса.
    /// </summary>
    /// <exception cref="InvalidOperationException">Путь содержит кавычку или оканчивается обратной косой чертой.</exception>
    public static void EnsureSafeForCommandLine(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Contains('"', StringComparison.Ordinal) || path.EndsWith('\\'))
        {
            // Сам путь в текст не попадает: имя файла пришло от пользователя и уходит в статус носителя.
            throw new InvalidOperationException(
                "Путь к файлу для ffmpeg содержит кавычку или оканчивается обратной косой чертой — такой путь нельзя "
                + "безопасно передать в командной строке ffprobe/ffmpeg; звук не извлекается.");
        }
    }

    // Объём данных WAV: от начала блока data до конца файла. Размер в заголовке не используется — у файла
    // больше 4 ГиБ он не помещается в поле и остаётся заглушкой; ffmpeg перед data пишет и служебные блоки (LIST).
    private static long ReadWaveDataBytes(string wavPath)
    {
        using var stream = File.OpenRead(wavPath);
        using var reader = new BinaryReader(stream, Encoding.ASCII);
        if (stream.Length < 12)
        {
            throw new InvalidOperationException("ffmpeg создал пустой или усечённый файл звука — проверьте сборку ffmpeg.");
        }

        var riff = reader.ReadBytes(4);
        reader.ReadUInt32(); // общий размер RIFF — по той же причине не используется
        var wave = reader.ReadBytes(4);
        if (!riff.AsSpan().SequenceEqual("RIFF"u8) || !wave.AsSpan().SequenceEqual("WAVE"u8))
        {
            throw new InvalidOperationException("ffmpeg создал файл звука не в формате WAV — проверьте сборку ffmpeg.");
        }

        while (stream.Position + 8 <= stream.Length)
        {
            var id = reader.ReadBytes(4);
            var size = reader.ReadUInt32();
            if (id.AsSpan().SequenceEqual("data"u8))
            {
                return stream.Length - stream.Position;
            }

            stream.Seek(size + (size & 1), SeekOrigin.Current); // блоки RIFF выровнены на чётную границу
        }

        throw new InvalidOperationException("ffmpeg создал WAV без блока данных — проверьте сборку ffmpeg.");
    }

    private static string SplitAdvice(TimeSpan limit) =>
        $"Расшифровка за один прогон не выполняется: разбейте запись на части не длиннее {FormatDuration(limit)} и загрузите их "
        + $"по отдельности. Предел — ключ {SpeechConfigurationKeys.MaxDurationHours} (не больше "
        + $"{SpeechOptions.MaxAllowedDurationHours.ToString(CultureInfo.InvariantCulture)} ч: предел детектора речи).";

    /// <summary>Длительность для текста ошибки: «24 ч», «1 ч 30 мин», «45 мин 10 с», «3 с».</summary>
    internal static string FormatDuration(TimeSpan value)
    {
        var hours = (long)value.TotalHours;
        var parts = new List<string>(3);
        if (hours > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{hours} ч"));
        }

        if (value.Minutes > 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{value.Minutes} мин"));
        }

        if (value.Seconds > 0 || parts.Count == 0)
        {
            parts.Add(string.Create(CultureInfo.InvariantCulture, $"{value.Seconds} с"));
        }

        return string.Join(' ', parts);
    }

    private string Describe(string what, Exception exception) =>
        $"{what}: {Shorten(exception.Message.Trim())} "
        + (string.IsNullOrWhiteSpace(_ffmpegFolder)
            ? $"(ffmpeg ищется в PATH; задайте каталог поставки в {SpeechConfigurationKeys.FfmpegFolder} или {SpeechConfigurationKeys.VisionFfmpegFolder}, deploy/offline/export-ffmpeg.ps1)."
            : $"(каталог ffmpeg: {Path.GetFullPath(_ffmpegFolder)}).");

    // Сообщение ffmpeg бывает многострочным и длинным; статусу носителя нужна суть, а не весь журнал.
    private static string Shorten(string message) =>
        message.Length <= MaxToolMessageLength ? message : message[..MaxToolMessageLength] + "…";
}
