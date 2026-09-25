using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Storage;
using ISC.AI.Modules.Media.Application.Features.Maintenance;
using ISC.AI.Modules.Media.Application.Features.Transcription;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Media;

/// <summary>
/// Конвейер расшифровки речи (ADR-0026): фрагменты записываются дословно одной транзакцией хранилища, событие
/// журнала — с грифом носителя и без субъекта (ТБ-030), сбой — в статус носителя без проброса, отмена —
/// пробрасывается, к изображению расшифровка неприменима. Распознаватель — фейк: модель в юнит-тестах не нужна.
/// </summary>
public sealed class MediaTranscriptionPipelineTests : IDisposable
{
    private const int AssetId = 9;
    private const string Model = "gigaam-multilingual-ctc@sha256:ab12";

    // Временные копии — в своём каталоге теста: настоящий %TEMP%\iscai-media тесты не трогают.
    private readonly MediaTempFiles _tempFiles = new(
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N")),
        Path.Combine(Path.GetTempPath(), "iscai-media-unit-tests", Guid.NewGuid().ToString("N") + "-legacy"));

    private readonly IMediaStore _store = Substitute.For<IMediaStore>();
    private readonly IFileStorage _files = Substitute.For<IFileStorage>();
    private readonly IAuditWriter _audit = Substitute.For<IAuditWriter>();
    private readonly FakeTranscriber _transcriber = new();

    public MediaTranscriptionPipelineTests()
    {
        _store.GetForTranscriptionAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetTranscriptionInfo(AssetId, MediaKind.Audio, "voice.ogg", Classification: 2, DivisionId: 7));
        _files.OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => (Stream)new MemoryStream([1, 2, 3]));
    }

    [Fact(DisplayName = "Успех: фрагменты записаны дословно (пустые отброшены), модель и длительность ЗАПИСИ (а не конец речи) переданы, журнал Ingest с грифом носителя без субъекта")]
    public async Task Success_writes_segments_verbatim_and_audits()
    {
        _transcriber.Segments =
        [
            new TranscriptSegmentDraft(0, 500, 2300, "мен үйгө бардым"),
            new TranscriptSegmentDraft(1, 3000, 3400, "   "),
            new TranscriptSegmentDraft(2, 4100, 7800, "ну давай завтра встретимся"),
        ];

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Segments.ShouldBe(2);
        result.Error.ShouldBeNull();

        await _store.Received(1).MarkTranscriptionProcessingAsync(AssetId, Arg.Any<CancellationToken>());
        await _store.Received(1).CompleteTranscriptionAsync(
            AssetId,
            Arg.Is<IReadOnlyList<TranscriptSegmentDraft>>(s =>
                s.Count == 2
                && s[0].Text == "мен үйгө бардым" && s[0].Index == 0 && s[0].StartMs == 500 && s[0].EndMs == 2300
                && s[1].Text == "ну давай завтра встретимся" && s[1].Index == 2),
            Model,
            45_000L, // длительность записи от распознавателя, а не конец последней речи (7800)
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailTranscriptionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());

        // Исходник взят из категории оригиналов, подкаталог — идентификатор носителя.
        await _files.Received(1).OpenReadAsync("voice.ogg", MediaFileCategories.Originals, "9", Arg.Any<CancellationToken>());

        // ТБ-030: одна запись, режим носителя, без субъекта; модель и число фрагментов — в чувствительной части,
        // вместе с пометкой «автоматическая, требует проверки» (ТЭ-002). Текста расшифровки в журнале нет.
        await _audit.Received(1).WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Action == AuditAction.Ingest
                && e.ObjectRef == "media:asset:9:transcribed"
                && e.Classification == 2 && e.DivisionId == 7 && e.SubjectId == null
                && e.PayloadSensitive!.Contains("фрагментов 2", StringComparison.Ordinal)
                && e.PayloadSensitive.Contains(Model, StringComparison.Ordinal)
                && e.PayloadSensitive.Contains("требует проверки", StringComparison.Ordinal)
                && !e.PayloadSensitive.Contains("үйгө", StringComparison.Ordinal)),
            Arg.Any<CancellationToken>());

        // Временная копия исходника — в управляемом каталоге (уборка при старте хоста, ТБ-064) с префиксом
        // конвейера — и удалена после распознавания.
        _transcriber.SourcePath.ShouldNotBeNull();
        File.Exists(_transcriber.SourcePath).ShouldBeFalse();
        Path.GetExtension(_transcriber.SourcePath).ShouldBe(".ogg");
        Path.GetDirectoryName(_transcriber.SourcePath).ShouldBe(_tempFiles.Root);
        Path.GetFileName(_transcriber.SourcePath).ShouldStartWith(MediaTempFiles.SpeechPrefix);
    }

    [Fact(DisplayName = "Имя исходника с кавычкой в расширении (сохранён до фильтра) → временная копия с расширением .bin: командная строка ffmpeg не разрывается")]
    public async Task Unsafe_stored_extension_becomes_bin_in_temp_copy()
    {
        _store.GetForTranscriptionAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetTranscriptionInfo(AssetId, MediaKind.Audio, "voice.m4a\" -y \"out", 2, 7));
        _transcriber.Segments = [new TranscriptSegmentDraft(0, 0, 900, "алло")];

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeTrue();
        _transcriber.SourcePath.ShouldNotBeNull().ShouldNotContain("\"");
        Path.GetExtension(_transcriber.SourcePath).ShouldBe(MediaFileNames.FallbackExtension);
        Path.GetDirectoryName(_transcriber.SourcePath).ShouldBe(_tempFiles.Root);
        File.Exists(_transcriber.SourcePath).ShouldBeFalse();
    }

    [Fact(DisplayName = "Речи не найдено: расшифровка записывается с нулём фрагментов, длительность записи всё равно передаётся")]
    public async Task No_speech_completes_with_zero_segments()
    {
        _transcriber.Segments = [];

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeTrue();
        result.Segments.ShouldBe(0);
        await _store.Received(1).CompleteTranscriptionAsync(
            AssetId, Arg.Is<IReadOnlyList<TranscriptSegmentDraft>>(s => s.Count == 0), Model, 45_000L, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Звуковой дорожки нет: ноль фрагментов, длительность неизвестна (null) — хранилище её не трогает")]
    public async Task No_audio_track_passes_unknown_duration()
    {
        _transcriber.Segments = [];
        _transcriber.DurationMs = null;

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeTrue();
        await _store.Received(1).CompleteTranscriptionAsync(
            AssetId, Arg.Is<IReadOnlyList<TranscriptSegmentDraft>>(s => s.Count == 0), Model, null, Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Сбой распознавателя: FailTranscriptionAsync с причиной, результат с ошибкой, исключение не пробрасывается, журнала нет")]
    public async Task Transcriber_failure_marks_failed_without_throwing()
    {
        _transcriber.Segments = [new TranscriptSegmentDraft(0, 0, 1000, "алло")];
        _transcriber.FailAfterSegments = new InvalidOperationException("Модель не прошла проверку SHA-256.");

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("Модель не прошла проверку SHA-256.");
        result.Segments.ShouldBe(0); // итог порта — целиком; частичный результат сбойного прогона не существует
        await _store.Received(1).FailTranscriptionAsync(AssetId, "Модель не прошла проверку SHA-256.", Arg.Any<CancellationToken>());
        await _store.DidNotReceive().CompleteTranscriptionAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<TranscriptSegmentDraft>>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        File.Exists(_transcriber.SourcePath!).ShouldBeFalse();
    }

    [Fact(DisplayName = "Отмена задачи: пробрасывается, носитель переведён в «расшифровка отменена», фрагменты не записаны")]
    public async Task Cancellation_is_rethrown_and_marked()
    {
        using var cts = new CancellationTokenSource();
        _transcriber.Segments = [new TranscriptSegmentDraft(0, 0, 1000, "алло")];
        _transcriber.OnSegment = cts.Cancel;

        await Should.ThrowAsync<OperationCanceledException>(() => Pipeline().TranscribeAsync(AssetId, cts.Token));

        await _store.Received(1).FailTranscriptionAsync(AssetId, MediaTranscriptionPipeline.CancelledReason, CancellationToken.None);
        await _store.DidNotReceive().CompleteTranscriptionAsync(
            Arg.Any<int>(), Arg.Any<IReadOnlyList<TranscriptSegmentDraft>>(), Arg.Any<string>(), Arg.Any<long?>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
        File.Exists(_transcriber.SourcePath!).ShouldBeFalse();
    }

    [Fact(DisplayName = "OperationCanceledException изнутри распознавателя без отмены задачи — обычный сбой с причиной, не проброс")]
    public async Task Foreign_cancellation_is_a_failure_not_a_cancel()
    {
        _transcriber.FailAfterSegments = new OperationCanceledException("Распознаватель не ответил за отведённое время.");

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("Распознаватель не ответил за отведённое время.");
        await _store.Received(1).FailTranscriptionAsync(AssetId, "Распознаватель не ответил за отведённое время.", Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Изображение: расшифровка неприменима — ни статуса, ни чтения исходника, ни распознавания, ни журнала")]
    public async Task Image_is_not_applicable()
    {
        _store.GetForTranscriptionAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetTranscriptionInfo(AssetId, MediaKind.Image, "photo.jpg", 2, 7));

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeFalse();
        result.Error.ShouldBe(MediaTranscriptionPipeline.NotApplicableToImageError);
        _transcriber.Calls.ShouldBe(0);
        await _store.DidNotReceive().MarkTranscriptionProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailTranscriptionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _files.DidNotReceive().OpenReadAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _audit.DidNotReceive().WriteAsync(Arg.Any<AuditEntry>(), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Видео: звуковая дорожка расшифровывается тем же конвейером")]
    public async Task Video_sound_track_is_transcribed()
    {
        _store.GetForTranscriptionAsync(AssetId, Arg.Any<CancellationToken>())
            .Returns(new MediaAssetTranscriptionInfo(AssetId, MediaKind.Video, "clip.mp4", 1, 7));
        _transcriber.Segments = [new TranscriptSegmentDraft(0, 100, 900, "стой")];

        var result = await Pipeline().TranscribeAsync(AssetId);

        result.Success.ShouldBeTrue();
        Path.GetExtension(_transcriber.SourcePath).ShouldBe(".mp4");
        await _audit.Received(1).WriteAsync(
            Arg.Is<AuditEntry>(e => e.Classification == 1 && e.ObjectRef == "media:asset:9:transcribed"), Arg.Any<CancellationToken>());
    }

    [Fact(DisplayName = "Носитель не найден: конвейер не запускается, статус не меняется")]
    public async Task Missing_asset_has_no_side_effects()
    {
        var result = await Pipeline().TranscribeAsync(123);

        result.Success.ShouldBeFalse();
        _transcriber.Calls.ShouldBe(0);
        await _store.DidNotReceive().MarkTranscriptionProcessingAsync(Arg.Any<int>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().FailTranscriptionAsync(Arg.Any<int>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempFiles.Root))
            {
                Directory.Delete(_tempFiles.Root, recursive: true);
            }
        }
        catch (IOException)
        {
            // Каталог теста — не повод валить прогон.
        }
    }

    private MediaTranscriptionPipeline Pipeline() =>
        new(_store, _files, _transcriber, _audit, _tempFiles, NullLogger<MediaTranscriptionPipeline>.Instance);

    /// <summary>
    /// Фейк распознавателя: отдаёт заданные фрагменты, запоминает путь к временной копии (и проверяет, что она
    /// существует в момент распознавания), по желанию падает после фрагментов или отменяет задачу на фрагменте.
    /// </summary>
    private sealed class FakeTranscriber : IAudioTranscriber
    {
        public IReadOnlyList<TranscriptSegmentDraft> Segments { get; set; } = [];

        public Exception? FailAfterSegments { get; set; }

        public Action? OnSegment { get; set; }

        public string? SourcePath { get; private set; }

        public int Calls { get; private set; }

        /// <summary>Длительность записи, которую «измерил» распознаватель (строка done).</summary>
        public long? DurationMs { get; set; } = 45_000;

        public string ModelVersion => Model;

        public async Task<AudioTranscription> TranscribeAsync(string sourcePath, CancellationToken cancellationToken = default)
        {
            Calls++;
            SourcePath = sourcePath;
            File.Exists(sourcePath).ShouldBeTrue("Распознаватель получает путь к уже записанной временной копии.");
            (await File.ReadAllBytesAsync(sourcePath, cancellationToken)).ShouldBe(new byte[] { 1, 2, 3 });

            foreach (var _ in Segments)
            {
                await Task.Yield();
                OnSegment?.Invoke();
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (FailAfterSegments is not null)
            {
                throw FailAfterSegments;
            }

            return new AudioTranscription(Segments, DurationMs);
        }
    }
}
