using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using ISC.AI.Evals.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.Evals.Speech.Tests;

/// <summary>
/// Эталонный набор (КИ-10): манифест <c>файл;язык;условия;примечание</c>, эталоны рядом с записями, все
/// проблемы сразу. Файлы записей — синтетические байты, реальных записей людей здесь нет (ТО-прог-13).
/// </summary>
public sealed class SpeechEvalDatasetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "iscai-speech-eval-tests", Guid.NewGuid().ToString("N"));

    public SpeechEvalDatasetTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    [Fact(DisplayName = "Манифест: заголовок, комментарии и пустые строки пропускаются; коды без учёта регистра")]
    public void Manifest_skips_header_comments_and_blank_lines()
    {
        var result = SpeechEvalManifest.Parse(
        [
            "﻿файл;язык;условия;примечание",
            "# пилот, первая партия",
            "",
            "call01.ogg;RU;Phone;голосовое",
            "interview/int02.m4a;mixed;interview",
        ]);

        result.Errors.ShouldBeEmpty();
        result.Entries.Count.ShouldBe(2);
        result.Entries[0].ShouldBe(new SpeechEvalManifestEntry(4, "call01.ogg", SpeechLanguage.Russian, RecordingCondition.Phone, "голосовое"));
        result.Entries[1].Language.ShouldBe(SpeechLanguage.Mixed);
        result.Entries[1].Note.ShouldBe(string.Empty);
    }

    [Fact(DisplayName = "Манифест: примечание в кавычках с «;» и удвоенной кавычкой; лишние «;» уходят в примечание")]
    public void Manifest_supports_quoted_fields()
    {
        var result = SpeechEvalManifest.Parse(
        [
            "a.wav;ky;street;\"шум; ветер, \"\"рынок\"\"\"",
            "b.wav;ky;street;шум; ветер",
        ]);

        result.Errors.ShouldBeEmpty();
        result.Entries[0].Note.ShouldBe("шум; ветер, \"рынок\"");
        result.Entries[1].Note.ShouldBe("шум; ветер");
    }

    [Fact(DisplayName = "Манифест: все ошибки сразу с номерами строк — язык, условия, мало полей, повтор файла, kg вместо ky")]
    public void Manifest_reports_all_errors_with_line_numbers()
    {
        var result = SpeechEvalManifest.Parse(
        [
            "a.wav;kg;phone",
            "b.wav;ru;car",
            "c.wav;ru",
            "d.wav;ru;phone",
            "D.WAV;ky;phone",
            "\"e.wav;ru;phone",
        ]);

        result.Entries.Count.ShouldBe(1);
        result.Errors.Count.ShouldBe(5);
        result.Errors[0].ShouldContain("строка 1");
        result.Errors[0].ShouldContain("ky");
        result.Errors[1].ShouldContain("строка 2");
        result.Errors[1].ShouldContain("car");
        result.Errors[2].ShouldContain("строка 3");
        result.Errors[3].ShouldContain("строка 5");
        result.Errors[3].ShouldContain("строке 4");
        result.Errors[4].ShouldContain("кавычка");
    }

    [Fact(DisplayName = "Набор: запись, эталон <имя>.txt рядом, язык и условия из манифеста; замечания к эталону")]
    public async Task Loads_dataset_with_references_and_warnings()
    {
        WriteManifest("файл;язык;условия;примечание", "calls/call01.ogg;ky;phone;синтетика", "memo.wav;ru;dictaphone;");
        WriteBytes("calls/call01.ogg", [1, 2, 3]);
        WriteText("calls/call01.txt", "Үйдө бала жок, 25 сом бар.");
        WriteBytes("memo.wav", [0]);
        WriteText("memo.txt", "Всё хорошо");
        WriteText("forgotten.txt", "эталон без записи в манифесте");

        var dataset = await SpeechEvalDataset.LoadAsync(_root);

        dataset.Recordings.Count.ShouldBe(2);
        var call = dataset.Recordings[0];
        call.File.ShouldBe("calls/call01.ogg");
        call.Language.ShouldBe(SpeechLanguage.Kyrgyz);
        call.Condition.ShouldBe(RecordingCondition.Phone);
        call.Note.ShouldBe("синтетика");
        call.ReferenceText.ShouldBe("Үйдө бала жок, 25 сом бар.");
        call.ReferencePath.ShouldBe(Path.Combine(_root, "calls", "call01.txt"));

        dataset.Warnings.ShouldContain(warning => warning.StartsWith("calls/call01.ogg:", StringComparison.Ordinal) && warning.Contains("25"));
        dataset.Warnings.ShouldContain(warning => warning.Contains("forgotten.txt"));
    }

    [Fact(DisplayName = "Набор: нет записи, нет эталона, путь за пределы папки, эталон не в UTF-8 — все проблемы в одном исключении")]
    public async Task Reports_all_dataset_problems_at_once()
    {
        WriteManifest("missing.wav;ru;phone", "noref.wav;ru;phone", "../outside.wav;ru;phone", "cp1251.wav;ru;phone");
        WriteBytes("noref.wav", [0]);
        WriteBytes("cp1251.wav", [0]);
        WriteBytes("cp1251.txt", [0xC0, 0xE1, 0xE2]); // «Абв» в Windows-1251 — не UTF-8

        var exception = await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalDataset.LoadAsync(_root));

        exception.Problems.Count.ShouldBe(4);
        exception.Problems.ShouldContain(problem => problem.Contains("missing.wav"));
        exception.Problems.ShouldContain(problem => problem.Contains("noref.txt"));
        exception.Problems.ShouldContain(problem => problem.Contains("../outside.wav"));
        exception.Problems.ShouldContain(problem => problem.Contains("UTF-8"));
    }

    [Fact(DisplayName = "Набор: две записи с одним именем и разными расширениями делят эталон — ошибка")]
    public async Task Rejects_two_recordings_sharing_one_reference()
    {
        WriteManifest("same.wav;ru;phone", "same.ogg;ru;phone");
        WriteBytes("same.wav", [0]);
        WriteBytes("same.ogg", [0]);
        WriteText("same.txt", "текст");

        var exception = await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalDataset.LoadAsync(_root));

        exception.Problems.ShouldHaveSingleItem().ShouldContain("same.txt");
    }

    [Fact(DisplayName = "Набор: нет манифеста — понятная ошибка с форматом")]
    public async Task Missing_manifest_is_reported()
    {
        var exception = await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalDataset.LoadAsync(_root));

        exception.Problems.ShouldHaveSingleItem().ShouldContain("manifest.csv");
    }

    [Fact(DisplayName = "Чтение текста: UTF-8 с BOM и без, UTF-16 с BOM; Windows-1251 отклоняется")]
    public void Decodes_utf8_and_utf16_and_rejects_legacy_encodings()
    {
        SpeechEvalDataset.DecodeText([0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("үй")]).ShouldBe("үй");
        SpeechEvalDataset.DecodeText(Encoding.UTF8.GetBytes("үй")).ShouldBe("үй");
        SpeechEvalDataset.DecodeText([0xFF, 0xFE, .. Encoding.Unicode.GetBytes("үй")]).ShouldBe("үй");
        SpeechEvalDataset.DecodeText([0xC0, 0xE1, 0xE2]).ShouldBeNull();
    }

    [Fact(DisplayName = "Замечания к эталону: цифры, латинские ö/ü/ñ вместо ө/ү/ң, смесь латиницы и кириллицы, чужие пометки в скобках")]
    public void Reference_lint_flags_common_mistakes()
    {
        ReferenceTextLint.Check("үйдө бала жок").ShouldBeEmpty();
        ReferenceTextLint.Check("үйдө [неразборчиво] жок").ShouldBeEmpty();

        ReferenceTextLint.Check("мне 25 лет").ShouldHaveSingleItem().ShouldContain("«25»");
        ReferenceTextLint.Check("üйдö").ShouldContain(warning => warning.Contains("«ө»"));
        ReferenceTextLint.Check("üйдö").ShouldContain(warning => warning.Contains("«ү»"));
        ReferenceTextLint.Check("cобака").ShouldHaveSingleItem().ShouldContain("латиницу и кириллицу");
        ReferenceTextLint.Check("да [шум] нет [неразборчево]").ShouldHaveSingleItem().ShouldContain("«[шум]», «[неразборчево]»");
        ReferenceTextLint.Check("[неразборчиво]").ShouldHaveSingleItem().ShouldContain("только из пометок");

        // Пустой эталон — не замечание, а ошибка набора (или контрольный файл без речи): см. тесты загрузки.
        ReferenceTextLint.Check("  ").ShouldBeEmpty();
    }

    [Fact(DisplayName = "Манифест: коды условий методики — phone, voice, interview, dictaphone, street, far, overlap, other")]
    public void Manifest_accepts_all_condition_codes()
    {
        var result = SpeechEvalManifest.Parse(
        [
            "a.wav;ru;phone", "b.wav;ru;voice", "c.wav;ky;interview", "d.wav;ky;dictaphone",
            "e.wav;mixed;street", "f.wav;mixed;far", "g.wav;mixed;overlap", "h.wav;ru;other",
        ]);

        result.Errors.ShouldBeEmpty();
        result.Entries.Select(entry => entry.Condition).ShouldBe(Enum.GetValues<RecordingCondition>());
        result.Entries.Select(entry => entry.Condition.ToCode())
            .ShouldBe(["phone", "voice", "interview", "dictaphone", "street", "far", "overlap", "other"]);
    }

    [Fact(DisplayName = "Манифест: частые ошибки кодов — понятная подсказка (mix → mixed, recorder → dictaphone), молча не принимаются")]
    public void Manifest_hints_for_common_code_mistakes()
    {
        var result = SpeechEvalManifest.Parse(["a.wav;mix;phone", "b.wav;ru;recorder", "c.wav;;phone"]);

        result.Entries.ShouldBeEmpty();
        result.Errors.Count.ShouldBe(3);
        result.Errors[0].ShouldContain("«mix»");
        result.Errors[0].ShouldContain("mixed");
        result.Errors[1].ShouldContain("«recorder»");
        result.Errors[1].ShouldContain("dictaphone");
        result.Errors[2].ShouldContain("ru | ky | mixed");
        result.Errors[2].ShouldContain("«-»");
    }

    [Fact(DisplayName = "Контрольный файл без речи: язык «-», эталон пуст или отсутствует — загружается без замечаний")]
    public async Task Loads_no_speech_control_files()
    {
        WriteManifest("файл;язык;условия;примечание", "silence.wav;-;dictaphone;тишина кабинета", "music.wav;-;other;", "ru.wav;ru;interview;");
        WriteBytes("silence.wav", [0]);
        WriteText("silence.txt", "  \r\n");
        WriteBytes("music.wav", [0]); // эталона нет вовсе — для контрольного файла допустимо
        WriteBytes("ru.wav", [0]);
        WriteText("ru.txt", "текст");

        var dataset = await SpeechEvalDataset.LoadAsync(_root);

        dataset.Recordings.Count.ShouldBe(3);
        dataset.Recordings[0].IsNoSpeech.ShouldBeTrue();
        dataset.Recordings[0].Language.ShouldBeNull();
        dataset.Recordings[1].IsNoSpeech.ShouldBeTrue();
        dataset.Recordings[1].ReferenceText.ShouldBe(string.Empty);
        dataset.Recordings[2].IsNoSpeech.ShouldBeFalse();
        dataset.Warnings.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Набор: пустой эталон речевой записи и непустой эталон у «-» — ошибки с подсказкой, а не молчаливая подмена")]
    public async Task Rejects_mismatch_between_empty_reference_and_no_speech_code()
    {
        WriteManifest("forgotten.wav;ky;phone", "noise.wav;-;street");
        WriteBytes("forgotten.wav", [0]);
        WriteText("forgotten.txt", " … ");
        WriteBytes("noise.wav", [0]);
        WriteText("noise.txt", "кто-то что-то сказал");

        var exception = await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalDataset.LoadAsync(_root));

        exception.Problems.Count.ShouldBe(2);
        exception.Problems[0].ShouldContain("forgotten.txt пуст");
        exception.Problems[0].ShouldContain("«-»");
        exception.Problems[1].ShouldContain("noise.txt не пуст");
    }

    [Fact(DisplayName = "Набор: папка hyp зарезервирована — запись в ней ошибка, её .txt не считаются забытыми эталонами")]
    public async Task Hyp_folder_is_reserved()
    {
        WriteManifest("a.wav;ru;phone", "hyp/b.wav;ru;phone");
        WriteBytes("a.wav", [0]);
        WriteText("a.txt", "да");
        WriteBytes("hyp/b.wav", [0]);
        WriteText("hyp/b.txt", "нет");

        var exception = await Should.ThrowAsync<SpeechEvalDatasetException>(() => SpeechEvalDataset.LoadAsync(_root));
        exception.Problems.ShouldHaveSingleItem().ShouldContain("зарезервирована");

        WriteManifest("a.wav;ru;phone");
        WriteText("hyp/vosk/a.txt", "да");
        var dataset = await SpeechEvalDataset.LoadAsync(_root);
        dataset.Warnings.ShouldBeEmpty();
    }

    private void WriteManifest(params string[] lines) =>
        File.WriteAllText(Path.Combine(_root, SpeechEvalManifest.FileName), string.Join("\r\n", lines), new UTF8Encoding(true));

    private void WriteText(string relativePath, string text) =>
        File.WriteAllText(FullPath(relativePath), text, new UTF8Encoding(false));

    private void WriteBytes(string relativePath, byte[] bytes) =>
        File.WriteAllBytes(FullPath(relativePath), bytes);

    private string FullPath(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        return path;
    }
}
