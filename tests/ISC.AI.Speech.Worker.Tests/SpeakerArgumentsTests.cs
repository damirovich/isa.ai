using System.IO;
using System.Linq;
using ISC.AI.Speech.Protocol;
using Shouldly;
using Xunit;

namespace ISC.AI.Speech.Worker.Tests;

/// <summary>
/// Разметка дикторов в процессе-распознавателе (ADR-0062 v0.3) без sherpa-onnx: модель «голосовых отпечатков» включает
/// разметку, ключи числа, порога и предела без неё — ошибка, числа — в границах, файл модели проверяется вместе с
/// остальными; строки реплик и исход разметки в <c>done</c> разбирает адаптер.
/// </summary>
public sealed class SpeakerArgumentsTests
{
    private static readonly string[] Base = ["--model", "m.onnx", "--tokens", "t.txt", "--vad", "v.onnx", "--input", "a.wav"];

    [Fact(DisplayName = "Дикторы: без ключей — разметки нет; с моделью отпечатков — включена, умолчания: число по порогу 0,4, до 120 мин")]
    public void Speaker_arguments_defaults()
    {
        var plain = WorkerArguments.Parse(Base);
        plain.Diarize.ShouldBeFalse();

        var args = WorkerArguments.Parse([.. Base, "--speaker-embedding", "emb.onnx"]);
        args.Diarize.ShouldBeTrue();
        args.SpeakerEmbeddingPath.ShouldBe("emb.onnx");
        WorkerArguments.DefaultSpeakerThreshold.ShouldBe(0.4);
        args.Speakers.ShouldBe(0);
        args.SpeakerThreshold.ShouldBe(WorkerArguments.DefaultSpeakerThreshold);
        args.SpeakerMaxMinutes.ShouldBe(WorkerArguments.DefaultSpeakerMaxMinutes);
    }

    [Fact(DisplayName = "Дикторы: число, порог (0,05–0,95) и предел — в границах; ключи дикторов без модели отпечатков — отказ с понятным текстом")]
    public void Speaker_arguments_validation()
    {
        var args = WorkerArguments.Parse(
            [.. Base, "--speaker-embedding", "e", "--speakers", "2", "--speaker-threshold", "0.7", "--speaker-max-minutes", "60"]);
        (args.Speakers, args.SpeakerThreshold, args.SpeakerMaxMinutes).ShouldBe((2, 0.7, 60));

        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speakers", "2"]))
            .Message.ShouldContain("--speaker-embedding");
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-segmentation", "s"]))
            .Message.ShouldContain("Неизвестный аргумент");
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-embedding", "e", "--speakers", "21"]));
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-embedding", "e", "--speaker-threshold", "0,5"]));
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-embedding", "e", "--speaker-threshold", "1.2"]));
        Should.Throw<WorkerUsageException>(() => WorkerArguments.Parse([.. Base, "--speaker-embedding", "e", "--speaker-max-minutes", "0"]));
    }

    [Fact(DisplayName = "Дикторы: отсутствующий файл модели отпечатков перечисляется вместе с остальными")]
    public void Speaker_model_files_are_checked()
    {
        var args = WorkerArguments.Parse([.. Base, "--speaker-embedding", "emb.onnx"]);

        var missing = args.FindMissingFiles(path => !path.StartsWith("emb", System.StringComparison.Ordinal));

        missing.ShouldHaveSingleItem().ShouldStartWith("--speaker-embedding:");
    }

    [Fact(DisplayName = "Протокол: записанные фрагменты запоминаются участками — по ним размечаются дикторы; пустой не запоминается")]
    public void Writer_keeps_segment_spans()
    {
        var writer = new ProtocolWriter(new StringWriter());
        writer.WriteSegment(0, 900, "салам");
        writer.WriteSegment(1_000, 1_500, "   ");
        writer.WriteSegment(2_000, 3_000, "кандай");

        writer.Spans.ShouldBe([new SegmentSpan(0, 900), new SegmentSpan(2_000, 3_000)]);
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
