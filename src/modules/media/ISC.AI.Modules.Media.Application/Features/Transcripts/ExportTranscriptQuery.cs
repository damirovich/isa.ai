using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Documents;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>Во что выгружается расшифровка (ADR-0047).</summary>
public enum TranscriptExportFormat
{
    /// <summary>Документ Word (<c>.docx</c>) с реквизитами ТБ-033 в теле и метаданных.</summary>
    Docx = 1,

    /// <summary>Текст для буфера обмена: маркировка грифа первой и последней строкой.</summary>
    Text = 2,
}

/// <summary>Выгруженная расшифровка: файл (<see cref="Content"/>) либо текст (<see cref="Text"/>).</summary>
/// <param name="FileName">Имя файла для сохранения (у текста — для справки).</param>
/// <param name="ContentType">MIME-тип содержимого.</param>
/// <param name="Content">Байты документа (<see cref="TranscriptExportFormat.Docx"/>); у текста — пусто.</param>
/// <param name="Text">Текст (<see cref="TranscriptExportFormat.Text"/>); у документа — <see langword="null"/>.</param>
public sealed record TranscriptExport(string FileName, string ContentType, byte[] Content, string? Text);

/// <summary>
/// Выгрузка расшифровки речи носителя (ADR-0047, ТФ-МЕД-18): «Word» — документ через порт ядра
/// <see cref="IDocumentExporter"/>, «Копировать» — текст в буфер обмена. Реквизиты ТБ-033 — гриф носителя, исполнитель —
/// и пометка «автоматическая расшифровка, требует проверки, не является протоколом» (ТЭ-002, ADR-0026) обязательны.
/// </summary>
/// <remarks>
/// АУДИТ (ТБ-030/032): каждая выгрузка — запись журнала <see cref="AuditAction.Export"/> с носителем и форматом, без
/// текста расшифровки; гриф записи — не ниже допуска субъекта (поведение аудита). Копирование в буфер — тоже выгрузка:
/// текст уходит за пределы системы на рабочем месте.
/// РЕЖИМ: та же решётка, что у карточки и расшифровки (floor ядра на стороне БД по носителю и каждому фрагменту, ТБ-020/021),
/// и ТОЛЬКО носитель дела субъекта (ТБ-071): материал чужого дела, открытый как кандидат его поиска (ADR-0044), —
/// только просмотр, выгрузить его нельзя, как и скачать оригинал. Без определённого сотрудника — отказ: исполнитель
/// документа обязателен (ТБ-033).
/// </remarks>
/// <param name="AssetId">Носитель (аудио или видео со звуком).</param>
/// <param name="Format">Документ или текст.</param>
public sealed record ExportTranscriptQuery(int AssetId, TranscriptExportFormat Format)
    : IRequest<ResponseDto<TranscriptExport>>, IAuditableRequest
{
    /// <summary>Единый ответ на «нет» и «нельзя» (ТБ-020/021).</summary>
    public const string NotFoundMessage = GetTranscriptQuery.NotFoundMessage;

    /// <summary>Отказ: носитель открыт только на просмотр (материал дела, которое субъект не ведёт).</summary>
    public const string ViewOnlyMessage =
        "Материал дела, которое вы не ведёте, открыт только на просмотр: выгрузить его расшифровку нельзя.";

    /// <summary>Отказ: расшифровки нет — выгружать нечего.</summary>
    public const string EmptyMessage = "Выгружать нечего: в расшифровке нет ни одного фрагмента.";

    /// <summary>Отказ: не удалось определить сотрудника (исполнитель документа обязателен, ТБ-033).</summary>
    public const string NoSubjectMessage = "Не удалось определить сотрудника: выгрузка без исполнителя не выполняется.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Export;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"media:asset:{AssetId.ToString(CultureInfo.InvariantCulture)}:transcript:export;format={FormatCode(Format)}";

    /// <summary>Код формата для журнала и имени файла.</summary>
    public static string FormatCode(TranscriptExportFormat format) => format switch
    {
        TranscriptExportFormat.Docx => "docx",
        TranscriptExportFormat.Text => "text",
        _ => "unknown",
    };

    /// <inheritdoc cref="ExportTranscriptQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        ISearchSessionStore sessions,
        ISubjectProvider subjectProvider,
        IUserAccountStore accounts,
        IDocumentExporter exporter,
        TimeProvider time) : IRequestHandler<ExportTranscriptQuery, ResponseDto<TranscriptExport>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<TranscriptExport>> Handle(ExportTranscriptQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — выгрузки нет.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // Сужение по делам ДО чтения: чужой носитель не читается вовсе; кандидат поиска — только просмотр (ADR-0044).
            var grant = await AssetViewAccess.ResolveAsync(caseScope, sessions, query.AssetId, access, cancellationToken);
            if (grant == AssetViewGrant.None)
            {
                return ResponseDto<TranscriptExport>.NotFound(NotFoundMessage);
            }

            if (grant != AssetViewGrant.Case)
            {
                return ResponseDto<TranscriptExport>.BadRequest(ViewOnlyMessage);
            }

            // Решётка гриф/подразделение — на стороне БД: по носителю и по каждому фрагменту.
            var asset = await catalog.GetAsync(query.AssetId, access, cancellationToken);
            var transcript = asset is null ? null : await catalog.GetTranscriptAsync(query.AssetId, access, cancellationToken);
            if (asset is null || transcript is null)
            {
                return ResponseDto<TranscriptExport>.NotFound(NotFoundMessage);
            }

            if (transcript.Segments.Count == 0)
            {
                return ResponseDto<TranscriptExport>.BadRequest(EmptyMessage);
            }

            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is not { } userId)
            {
                return ResponseDto<TranscriptExport>.BadRequest(NoSubjectMessage);
            }

            var account = (await accounts.ListAsync(cancellationToken)).FirstOrDefault(a => a.UserId == userId);
            var executor = account is null
                ? string.Create(CultureInfo.InvariantCulture, $"пользователь № {userId}")
                : string.IsNullOrWhiteSpace(account.DisplayName) ? account.UserName : account.DisplayName;

            var caseId = await caseScope.GetCaseIdForAssetAsync(asset.Id, access, cancellationToken);
            var caseItem = caseId is { } id ? await caseScope.GetCaseAsync(id, access, cancellationToken) : null;

            var document = TranscriptExportText.Compose(
                asset, transcript, caseItem?.Number, executor, time.GetLocalNow().DateTime);
            var fileBase = string.Create(CultureInfo.InvariantCulture, $"Расшифровка носителя {asset.Id}");

            if (query.Format == TranscriptExportFormat.Text)
            {
                var text = TranscriptExportText.ToPlainText(document);
                return ResponseDto<TranscriptExport>.Ok(new TranscriptExport(fileBase + ".txt", "text/plain", [], text));
            }

            var bytes = await exporter.ExportToDocxAsync(
                new DocumentExportRequest(
                    document.Title, document.Body, document.Marking, Reference: null, document.Executor, document.Notice),
                cancellationToken);
            return ResponseDto<TranscriptExport>.Ok(new TranscriptExport(
                fileBase + ".docx", "application/vnd.openxmlformats-officedocument.wordprocessingml.document", bytes, null));
        }
    }
}

/// <summary>Содержимое выгрузки расшифровки: заголовок, маркировка грифа, пометка, тело и исполнитель (ТБ-033).</summary>
/// <param name="Title">Заголовок документа.</param>
/// <param name="Marking">Маркировка грифа носителя («НЕСЕКРЕТНО», «ДЛЯ СЛУЖЕБНОГО ПОЛЬЗОВАНИЯ», …).</param>
/// <param name="Notice">Пометка «автоматическая расшифровка…» — обязательна (ТЭ-002, ADR-0026).</param>
/// <param name="Body">Реквизиты носителя и фрагменты «[мм:сс] текст» — абзацы через перевод строки.</param>
/// <param name="Executor">Исполнитель — кто выгрузил.</param>
public sealed record TranscriptExportDocument(string Title, string Marking, string Notice, string Body, string Executor);

/// <summary>
/// Сборка содержимого выгрузки расшифровки (ADR-0047) — отдельно от обработчика, чтобы проверять без портов. Текст
/// фрагментов — ДОСЛОВНО (первичный слой, ТЭ-002); переводы строк внутри фрагмента заменяются пробелом, чтобы фрагмент
/// оставался одной строкой с таймкодом.
/// </summary>
public static class TranscriptExportText
{
    /// <summary>Заголовок документа.</summary>
    public const string Title = "Расшифровка речи";

    /// <summary>Обязательная пометка: текст модели — не протокол и требует проверки человеком.</summary>
    public const string Notice =
        "АВТОМАТИЧЕСКАЯ РАСШИФРОВКА: дословный текст модели распознавания речи, без знаков препинания. "
        + "Требует проверки и не является протоколом.";

    /// <summary>Собрать содержимое выгрузки.</summary>
    /// <param name="asset">Носитель (имя файла, гриф, длительность).</param>
    /// <param name="transcript">Расшифровка с фрагментами.</param>
    /// <param name="caseNumber">Номер дела носителя; <see langword="null"/> — не привязан или недоступен.</param>
    /// <param name="executor">Кто выгружает.</param>
    /// <param name="exportedAt">Когда (местное время сервера).</param>
    public static TranscriptExportDocument Compose(
        MediaAssetRow asset, MediaTranscript transcript, string? caseNumber, string executor, DateTime exportedAt)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ArgumentNullException.ThrowIfNull(transcript);

        var body = new StringBuilder();
        body.Append(CultureInfo.InvariantCulture, $"Носитель № {asset.Id}: {OneLine(asset.OriginalFileName)}").Append('\n');
        body.Append("Дело: ").Append(caseNumber is null ? "не указано" : "№ " + OneLine(caseNumber)).Append('\n');
        if (asset.DurationMs is { } duration)
        {
            body.Append("Длительность записи: ").Append(Clock(duration)).Append('\n');
        }

        body.Append("Модель распознавания: ").Append(string.IsNullOrWhiteSpace(transcript.ModelVersion) ? "не указана" : transcript.ModelVersion);
        if (transcript.TranscribedAt is { } at)
        {
            body.Append(CultureInfo.InvariantCulture, $"; расшифровано {at.ToLocalTime():dd.MM.yyyy HH:mm}");
        }

        body.Append('\n');
        body.Append(CultureInfo.InvariantCulture, $"Выгружено: {exportedAt:dd.MM.yyyy HH:mm}").Append('\n');
        body.Append('\n');
        var ordered = transcript.Segments.OrderBy(s => s.StartMs).ThenBy(s => s.Index).ToList();
        if (ordered.Any(s => s.SpeakerLabel is not null))
        {
            // По репликам (ADR-0062): подряд идущие фрагменты одного диктора — одна реплика с его подписью и таймкодом
            // начала; текст — дословно (ТЭ-007).
            foreach (var replica in Replicas(ordered))
            {
                body.Append(replica.Label ?? "Диктор не определён").Append(" [").Append(Clock(replica.StartMs)).Append("]: ")
                    .Append(string.Join(" ", replica.Texts.Select(OneLine))).Append('\n');
            }
        }
        else
        {
            foreach (var segment in ordered)
            {
                body.Append('[').Append(Clock(segment.StartMs)).Append("] ").Append(OneLine(segment.Text)).Append('\n');
            }
        }

        var marking = ClassificationLevels.Marking(asset.Classification);
        return new TranscriptExportDocument(Title, marking, Notice, body.ToString().TrimEnd('\n'), executor);
    }

    /// <summary>
    /// Текст для буфера обмена: маркировка грифа первой и последней строкой (ТБ-033), заголовок, пометка, тело и
    /// исполнитель. Переводы строк — CRLF: так текст вставляется одинаково в Word и в «Блокнот».
    /// </summary>
    public static string ToPlainText(TranscriptExportDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        var lines = new List<string> { document.Marking, document.Title, document.Notice, string.Empty };
        lines.AddRange(document.Body.Split('\n'));
        lines.Add(string.Empty);
        lines.Add("Исполнитель: " + document.Executor);
        lines.Add(document.Marking);
        return string.Join("\r\n", lines);
    }

    /// <summary>Реплика выгрузки: подпись диктора, начало и тексты подряд идущих фрагментов.</summary>
    /// <param name="Label">Подпись диктора; <see langword="null"/> — фрагмент без диктора.</param>
    /// <param name="StartMs">Начало реплики.</param>
    /// <param name="Texts">Тексты фрагментов по порядку.</param>
    public sealed record Replica(string? Label, long StartMs, IReadOnlyList<string> Texts);

    /// <summary>Склейка фрагментов в реплики: подряд идущие фрагменты одного действующего диктора (ADR-0062).</summary>
    public static IReadOnlyList<Replica> Replicas(IReadOnlyList<TranscriptSegmentRow> ordered)
    {
        ArgumentNullException.ThrowIfNull(ordered);
        var result = new List<Replica>();
        foreach (var segment in ordered)
        {
            if (result.Count > 0 && result[^1].Label == segment.SpeakerLabel && segment.SpeakerLabel is not null)
            {
                var last = result[^1];
                result[^1] = last with { Texts = [.. last.Texts, segment.Text] };
                continue;
            }

            result.Add(new Replica(segment.SpeakerLabel, segment.StartMs, [segment.Text]));
        }

        return result;
    }

    /// <summary>Таймкод «мм:сс» (от часа — «ч:мм:сс»), как в карточке носителя.</summary>
    public static string Clock(long milliseconds)
    {
        var span = TimeSpan.FromMilliseconds(Math.Max(0, milliseconds));
        return span.TotalHours >= 1
            ? ((int)span.TotalHours).ToString(CultureInfo.InvariantCulture) + ":" + span.ToString(@"mm\:ss", CultureInfo.InvariantCulture)
            : span.ToString(@"mm\:ss", CultureInfo.InvariantCulture);
    }

    // Фрагмент и реквизит — одной строкой: перевод строки внутри разорвал бы пару «таймкод — текст».
    private static string OneLine(string? value) =>
        (value ?? string.Empty).Replace("\r\n", " ", StringComparison.Ordinal).Replace('\r', ' ').Replace('\n', ' ');
}
