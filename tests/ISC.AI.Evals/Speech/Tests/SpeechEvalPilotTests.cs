using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>
/// Пилот одной командой (КИ-10): набор с диска → распознаватель → отчёт в папку. Записи — синтетическая
/// тишина в WAV, «модель» — подделка; реальных записей людей нет (ТО-прог-13).
/// </summary>
public sealed class SpeechEvalPilotTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-tests", Guid.NewGuid().ToString("N"));

    public SpeechEvalPilotTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "set"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact(DisplayName = "Пилот: набор загружается, записи расшифровываются, длительность WAV — из заголовка, отчёт записан")]
    public async Task Runs_dataset_end_to_end()
    {
        var set = Path.Combine(_root, "set");
        await File.WriteAllTextAsync(Path.Combine(set, "manifest.csv"), "файл;язык;условия;примечание\r\nru.wav;ru;dictaphone;\r\nky.wav;ky;phone;\r\n", Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(set, "ru.wav"), SilentWav(seconds: 2));
        await File.WriteAllTextAsync(Path.Combine(set, "ru.txt"), "Проверка связи.");
        await File.WriteAllBytesAsync(Path.Combine(set, "ky.wav"), SilentWav(seconds: 4));
        await File.WriteAllTextAsync(Path.Combine(set, "ky.txt"), "Үйдө бала жок.");
        var transcriber = new EchoTranscriber(new Dictionary<string, string>
        {
            ["ru.wav"] = "проверка связи",
            ["ky.wav"] = "уйдо бала жок",
        });
        var output = Path.Combine(_root, "out");

        var report = await SpeechEvalPilot.RunAndExportAsync(transcriber, set, output, ffprobeFolder: Path.Combine(_root, "no-ffprobe"));

        report.ModelVersion.ShouldBe(EchoTranscriber.Version);
        report.Records.Count.ShouldBe(2);
        report.Records[0].WordErrorRate.ShouldBe(0.0);
        report.Records[0].AudioDuration.ShouldBe(TimeSpan.FromSeconds(2));
        report.Records[0].DurationSource.ShouldBe(DurationSource.Measured);
        report.Records[1].WordErrorRate!.Value.ShouldBe(1.0 / 3, 1e-12);
        File.Exists(Path.Combine(output, "report.md")).ShouldBeTrue();
        File.Exists(Path.Combine(output, "transcripts", "ky.hyp.words.txt")).ShouldBeTrue();
    }

    [Fact(DisplayName = "Пилот: папка отчёта внутри набора отклоняется ДО прогона — распознаватель не вызывается")]
    public async Task Rejects_output_inside_dataset_before_running()
    {
        var set = Path.Combine(_root, "set");
        await File.WriteAllTextAsync(Path.Combine(set, "manifest.csv"), "a.wav;ru;phone", Encoding.UTF8);
        await File.WriteAllBytesAsync(Path.Combine(set, "a.wav"), SilentWav(seconds: 1));
        await File.WriteAllTextAsync(Path.Combine(set, "a.txt"), "да");
        var transcriber = new EchoTranscriber(new Dictionary<string, string> { ["a.wav"] = "да" });

        await Should.ThrowAsync<ArgumentException>(
            () => SpeechEvalPilot.RunAndExportAsync(transcriber, set, Path.Combine(set, "report")));

        transcriber.Calls.ShouldBe(0);
    }

    // Синтетическая тишина: WAV 16 кГц, моно, 16 бит.
    private static byte[] SilentWav(int seconds)
    {
        const int sampleRate = 16_000;
        var dataBytes = sampleRate * 2 * seconds;
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);
        writer.Write((short)2);
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataBytes);
        writer.Write(new byte[dataBytes]);
        writer.Flush();
        return stream.ToArray();
    }

    /// <summary>«Модель», которая отдаёт заранее заданный текст одним фрагментом.</summary>
    private sealed class EchoTranscriber(IReadOnlyDictionary<string, string> texts) : IAudioTranscriber
    {
        public const string Version = "echo@000000000000";

        public int Calls { get; private set; }

        public string ModelVersion => Version;

        public async Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            await Task.Yield();
            return new AudioTranscription([new TranscriptSegmentDraft(0, 0, 1_000, texts[Path.GetFileName(sourcePath)])], 1_000);
        }
    }
}
