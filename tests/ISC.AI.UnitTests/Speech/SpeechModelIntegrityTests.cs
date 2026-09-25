using System;
using System.IO;
using System.Security.Cryptography;
using ISC.AI.Speech;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Speech;

/// <summary>
/// Проверка пинов SHA-256 файлов модели речи (ТИ-004, ТБ-051; ADR-0026): без пина, с чужим хешем, без
/// файла — явный отказ с указанием ключа конфигурации и полного пути; совпавший пин — полный путь.
/// </summary>
public sealed class SpeechModelIntegrityTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "iscai-speech-pin-" + Guid.NewGuid().ToString("N"));

    public SpeechModelIntegrityTests() => Directory.CreateDirectory(_dir);

    public void Dispose() => Directory.Delete(_dir, recursive: true);

    [Fact(DisplayName = "ТИ-004: совпавший пин (регистр не важен) — возвращается ПОЛНЫЙ путь к файлу")]
    public void Matching_pin_returns_full_path()
    {
        var path = WriteFile("model.onnx", [1, 2, 3, 4]);
        var sha = Convert.ToHexString(SHA256.HashData(new byte[] { 1, 2, 3, 4 }));

        SpeechModelIntegrity.EnsureTrusted(path, sha.ToLowerInvariant(), "модель", "Speech:Model:Path", "Speech:Model:Sha256")
            .ShouldBe(Path.GetFullPath(path));
        SpeechModelIntegrity.EnsureTrusted(path, "  " + sha + " ", "модель", "Speech:Model:Path", "Speech:Model:Sha256")
            .ShouldBe(Path.GetFullPath(path));
    }

    [Fact(DisplayName = "ТБ-051: подменённый файл (чужой SHA-256) — отказ с обоими хешами и ключом пина")]
    public void Tampered_file_is_rejected()
    {
        var path = WriteFile("model.onnx", [9, 9, 9]);

        var error = Should.Throw<InvalidOperationException>(() =>
            SpeechModelIntegrity.EnsureTrusted(path, "DEADBEEF", "модель распознавания речи", "Speech:Model:Path", "Speech:Model:Sha256"));

        error.Message.ShouldContain("Целостность");
        error.Message.ShouldContain("DEADBEEF");
        error.Message.ShouldContain(Convert.ToHexString(SHA256.HashData(new byte[] { 9, 9, 9 })));
        error.Message.ShouldContain("Speech:Model:Sha256");
    }

    [Fact(DisplayName = "ТИ-004: без пина файл не используется — отказ с ключом пина")]
    public void Missing_pin_is_rejected()
    {
        var path = WriteFile("vad.onnx", [1]);

        Should.Throw<InvalidOperationException>(() =>
                SpeechModelIntegrity.EnsureTrusted(path, " ", "детектор речи", "Speech:Vad:Path", "Speech:Vad:Sha256"))
            .Message.ShouldContain("Speech:Vad:Sha256");
    }

    [Fact(DisplayName = "Нет пути — «Расшифровка не настроена» с ключом пути")]
    public void Missing_path_is_not_configured()
    {
        Should.Throw<InvalidOperationException>(() =>
                SpeechModelIntegrity.EnsureTrusted("", "00", "словарь", "Speech:Tokens:Path", "Speech:Tokens:Sha256"))
            .Message.ShouldStartWith("Расшифровка не настроена");
    }

    [Fact(DisplayName = "Нет файла по относительному пути — в сообщении полный путь и рабочий каталог, от которого он считался")]
    public void Missing_relative_file_names_full_path_and_current_directory()
    {
        var relative = Path.Combine("нет-такой-папки-" + Guid.NewGuid().ToString("N"), "model.onnx");

        var error = Should.Throw<FileNotFoundException>(() =>
            SpeechModelIntegrity.EnsureTrusted(relative, "00", "модель", "Speech:Model:Path", "Speech:Model:Sha256"));

        error.Message.ShouldContain(Path.GetFullPath(relative));
        error.Message.ShouldContain(Directory.GetCurrentDirectory());
        error.Message.ShouldContain("Speech:Model:Path");
        error.FileName.ShouldBe(Path.GetFullPath(relative));
    }

    private string WriteFile(string name, byte[] content)
    {
        var path = Path.Combine(_dir, name);
        File.WriteAllBytes(path, content);
        return path;
    }
}
