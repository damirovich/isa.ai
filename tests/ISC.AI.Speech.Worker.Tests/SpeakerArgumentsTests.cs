using System.IO;
using System.Linq;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Разметка дикторов в процессе-распознавателе (ADR-0062) без sherpa-onnx: ключи моделей говорящих — только парой, числа —
/// в границах, файлы моделей проверяются вместе с остальными; строки реплик и исход разметки в <c>done</c> разбирает
/// адаптер.
/// </summary>
public sealed class SpeakerArgumentsTests
{
    private static readonly string[] Base = ["--model", "m.onnx", "--tokens", "t.txt", "--vad", "v.onnx", "--input", "a.wav"];

    [Fact(DisplayName = "Дикторы: без ключей — разметки нет; с парой моделей — включена, умолчания: число по порогу 0,5, до 120 мин")]
    public void Speaker_arguments_defaults()
    {
        var plain = WorkerArguments.Parse(Base);
        plain.Diarize.ShouldBeFalse();

        var args = WorkerArguments.Parse([.. Base, "--speaker-segmentation", "seg.onnx", "--speaker-embedding", "emb.onnx"]);
        args.Diarize.ShouldBeTrue();
        args.SpeakerSegmentationPath.ShouldBe("seg.onnx");
        args.SpeakerEmbeddingPath.ShouldBe("emb.onnx");
        args.Speakers.ShouldBe(0);
        args.SpeakerThreshold.ShouldBe(WorkerArguments.DefaultSpeakerThreshold);
        args.SpeakerMaxMinutes.ShouldBe(WorkerArguments.DefaultSpeakerMaxMinutes);
    }

    [Fact(DisplayName = "Дикторы: число, порог и предел — в границах; одна модель без другой — отказ с понятным текстом")]
    public void Speaker_arguments_validation()
    {
        var args = WorkerArguments.Parse(
            [.. Base, "--speaker-segmentation", "s", "--speaker-embedding", "e", "--speakers", "2", "--speaker-threshold", "0.7", "--speaker-max-minutes", "60"]);
        (args.Speakers, args.SpeakerThreshold, args.SpeakerMaxMinutes).ShouldBe((2, 0.7, 60));

        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-segmentation", "s"]))
            .Message.ShouldContain("парой");
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-segmentation", "s", "--speaker-embedding", "e", "--speakers", "21"]));
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-segmentation", "s", "--speaker-embedding", "e", "--speaker-threshold", "0,5"]));
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-segmentation", "s", "--speaker-embedding", "e", "--speaker-max-minutes", "0"]));
    }

    [Fact(DisplayName = "Дикторы: отсутствующие файлы моделей говорящих перечисляются вместе с остальными")]
    public void Speaker_model_files_are_checked()
    {
        var args = WorkerArguments.Parse([.. Base, "--speaker-segmentation", "seg.onnx", "--speaker-embedding", "emb.onnx"]);

        var missing = args.FindMissingFiles(path => !path.StartsWith("seg", System.StringComparison.Ordinal) && !path.StartsWith("emb", System.StringComparison.Ordinal));

        missing.Count.ShouldBe(2);
        missing.ShouldContain(m => m.StartsWith("--speaker-segmentation:", System.StringComparison.Ordinal));
        missing.ShouldContain(m => m.StartsWith("--speaker-embedding:", System.StringComparison.Ordinal));
    }

    [Fact(DisplayName = "Протокол: реплики и исход разметки в done пишет утилита и разбирает адаптер; без разметки — поля нет")]
    public void Turn_lines_round_trip()
    {
        var output = new StringWriter { NewLine = "\n" };
        var writer = new ProtocolWriter(output);
        writer.WriteSegment(0, 900, "салам");
        writer.WriteTurn(0, 1_200, 1);
        writer.WriteTurn(1_200, 2_000, 0);
        writer.WriteDone(2_000, "done");

        var parser = new SpeechWorkerOutputParser();
        foreach (var line in output.ToString().Split('\n'))
        {
            parser.Accept(line);
        }

        parser.EnsureCompleted();
        parser.Turns.Select(t => (t.StartMs, t.EndMs, t.Speaker)).ShouldBe([(0L, 1_200L, 1), (1_200L, 2_000L, 0)]);
        parser.Done!.Diarization.ShouldBe(SpeechWorkerProtocol.DiarizationDone);

        var skipped = new StringWriter { NewLine = "\n" };
        new ProtocolWriter(skipped).WriteDone(9_000_000, "skipped", "запись длиннее 120 мин");
        var done = SpeechWorkerProtocol.ParseLine(skipped.ToString().Trim()).ShouldBeOfType<SpeechWorkerDone>();
        (done.Diarization, done.DiarizationNote).ShouldBe(("skipped", "запись длиннее 120 мин"));

        var plain = new StringWriter { NewLine = "\n" };
        new ProtocolWriter(plain).WriteDone(1_000);
        plain.ToString().ShouldNotContain("diarization");
    }
}
