using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;

namespace ISC.AI.Modules.Media.Application.Features.Import;

/// <summary>Дело в итоге проверки: что оператор подтверждает пачкой (ТБ-024) и сколько в него ляжет.</summary>
/// <param name="Target">Дело с подразделением и отделом.</param>
/// <param name="FileCount">Сколько файлов описи ляжет в дело (носители и документы).</param>
/// <param name="Bytes">Их объём, байт.</param>
/// <param name="DocumentCount">Из них документов (ADR-0042).</param>
public sealed record MediaImportCaseSummary(CaseImportTarget Target, int FileCount, long Bytes, int DocumentCount = 0);

/// <summary>Строка описи, которая не пройдёт импорт, и почему.</summary>
/// <param name="Row">Строка описи как есть — для выгрузки в формате описи.</param>
/// <param name="Message">Причины через «; ».</param>
public sealed record MediaImportProblem(ManifestRow Row, string Message);

/// <summary>Итог проверки описи (ADR-0041 п. 3): что будет импортировано и что нет. Ничего не записано.</summary>
/// <param name="FolderPath">Папка архива.</param>
/// <param name="ManifestFileName">Имя описи.</param>
/// <param name="ManifestSha256">SHA-256 описи.</param>
/// <param name="Items">Строки, прошедшие проверку.</param>
/// <param name="Problems">Строки с ошибками.</param>
/// <param name="Cases">Дела, в которые лягут файлы.</param>
/// <param name="TotalBytes">Объём проходящих строк, байт.</param>
/// <param name="DateOnlyCount">Строк с датой без времени (будет 00:00).</param>
/// <param name="DateFromMetadataCount">Фото и видео без даты в описи — дата будет взята из метаданных файла.</param>
/// <param name="IgnoredColumns">Лишние столбцы описи.</param>
/// <param name="EncodingName">Кодировка описи.</param>
/// <param name="Fingerprint">
/// Отпечаток итога: при запуске проверка повторяется, и задание создаётся, только если итог тот же, что видел и
/// подтвердил оператор (гриф и подразделение каждого дела, число строк).
/// </param>
/// <param name="DocumentCount">Документов к приёму (ADR-0042).</param>
/// <param name="DocumentDateFromFileCount">Документов без даты в описи — дата будет датой изменения файла.</param>
public sealed record MediaImportPlan(
    string FolderPath,
    string ManifestFileName,
    string ManifestSha256,
    IReadOnlyList<MediaImportItemDraft> Items,
    IReadOnlyList<MediaImportProblem> Problems,
    IReadOnlyList<MediaImportCaseSummary> Cases,
    long TotalBytes,
    int DateOnlyCount,
    int DateFromMetadataCount,
    IReadOnlyList<string> IgnoredColumns,
    string EncodingName,
    string Fingerprint,
    int DocumentCount = 0,
    int DocumentDateFromFileCount = 0);

/// <summary>
/// Проверка описи архива до запуска (ADR-0041 п. 3): разбирает строки, находит файлы под папкой архива
/// (<see cref="MediaImportPaths"/>), сопоставляет номера дел с делами, ДОСТУПНЫМИ оператору (ТБ-020/071), и считает итог
/// по делам. Ничего не записывает. Сигнатура содержимого и дата из метаданных проверяются при приёме файла: читать
/// начало каждого файла архива на проверке — часы работы диска.
/// </summary>
/// <remarks>
/// Документы (ADR-0042) проверяются здесь же: тип — из описи по наименованию или общий для задания (только типы,
/// которые принимает профиль, <see cref="IArchiveDocumentSink"/>), дата — из описи, иначе дата изменения файла,
/// название — из описи, иначе имя файла.
/// </remarks>
public sealed class MediaImportPlanner(
    ICaseScope caseScope, MediaUploadOptions? uploadOptions = null, IArchiveDocumentSink? documents = null)
{
    /// <summary>Предел длины пути файла относительно папки архива.</summary>
    public const int MaxRelativePathLength = MediaImportLimits.RelativePathMaxLength;

    /// <summary>Строит итог проверки.</summary>
    /// <param name="folder">Полный путь папки архива (проверен <see cref="MediaImportPaths.ResolveFolder"/>).</param>
    /// <param name="manifestFileName">Имя файла описи.</param>
    /// <param name="manifest">Прочитанная опись без ошибки чтения.</param>
    /// <param name="access">Допуск оператора.</param>
    /// <param name="zone">Пояс для дат описи без смещения.</param>
    /// <param name="nowUtc">Текущее время (даты из будущего — ошибка).</param>
    /// <param name="defaultDocumentTypeId">Тип для документов, у которых в описи тип не указан.</param>
    /// <param name="progress">Сколько строк проверено.</param>
    /// <param name="cancellationToken">Отмена.</param>
    public async Task<MediaImportPlan> PlanAsync(
        string folder,
        string manifestFileName,
        ManifestReadResult manifest,
        AccessContext access,
        TimeZoneInfo zone,
        DateTimeOffset nowUtc,
        int? defaultDocumentTypeId = null,
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(folder);
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(access);
        ArgumentNullException.ThrowIfNull(zone);
        if (manifest.Error is not null)
        {
            throw new ArgumentException("Опись с ошибкой чтения не проверяется.", nameof(manifest));
        }

        var options = uploadOptions ?? MediaUploadOptions.Default;
        var numbers = manifest.Rows
            .Select(r => r.CaseNumber?.Trim())
            .Where(n => !string.IsNullOrEmpty(n))
            .Select(n => n!)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        // Дела — только доступные оператору: недоступное неотличимо от несуществующего (ТБ-021).
        var targets = numbers.Count == 0
            ? []
            : await caseScope.FindCasesByNumberAsync(numbers, access, cancellationToken);
        var byNumber = targets
            .GroupBy(t => t.Case.Number.Trim(), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.Ordinal);

        var sink = documents;
        var documentTypes = sink is { IsSupported: true }
            ? await sink.ListTypesAsync(cancellationToken)
            : [];
        var typesByName = documentTypes
            .GroupBy(t => NormalizeName(t.Name), StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => g.First().Id, StringComparer.Ordinal);
        var defaultType = defaultDocumentTypeId is { } wantedType && documentTypes.Any(t => t.Id == wantedType) ? wantedType : (int?)null;

        var paths = new MediaImportPaths();
        var seen = new Dictionary<string, int>(MediaImportPaths.PathComparer);
        var items = new List<MediaImportItemDraft>();
        var problems = new List<MediaImportProblem>();
        var dateOnly = 0;
        var fromMetadata = 0;
        var documentCount = 0;
        var documentDateFromFile = 0;

        for (var index = 0; index < manifest.Rows.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index % 500 == 0)
            {
                progress?.Report(index);
            }

            var row = manifest.Rows[index];
            var errors = new List<string>();

            // --- файл ---
            MediaImportSourceFile? file = null;
            string? contentType = null;
            MediaImportFileKind? kind = null;
            if (row.File is null)
            {
                errors.Add("не указан файл");
            }
            else if (row.File.Length > MaxRelativePathLength)
            {
                errors.Add($"путь длиннее {MaxRelativePathLength} символов");
            }
            else
            {
                file = paths.ResolveFile(folder, row.File, out var fileProblem);
                if (file is null)
                {
                    errors.Add(fileProblem!);
                }
                else if (seen.TryGetValue(file.RelativePath, out var firstRow))
                {
                    errors.Add($"этот файл уже указан в строке {firstRow}");
                    file = null;
                }
                else
                {
                    seen[file.RelativePath] = row.RowNumber;
                    contentType = MediaFileRules.ContentTypeForFile(file.RelativePath);
                    kind = MediaFileRules.KindOf(contentType)?.ToImportKind();
                    if (kind is null && ArchiveDocumentFormats.ContentTypeForFile(file.RelativePath) is { } documentType)
                    {
                        contentType = documentType;
                        kind = MediaImportFileKind.Document;
                    }

                    var maxBytes = kind switch
                    {
                        null => 0,
                        MediaImportFileKind.Document => MediaImportLimits.DocumentMaxBytes,
                        _ => options.MaxBytesFor(kind.Value.ToMediaKind()!.Value),
                    };
                    if (kind is null)
                    {
                        errors.Add("формат файла не поддерживается (" + Path.GetExtension(file.RelativePath) + ")");
                    }
                    else if (kind == MediaImportFileKind.Document && sink is not { IsSupported: true })
                    {
                        errors.Add(UnsupportedArchiveDocumentSink.NotSupportedMessage);
                    }
                    else if (file.Length == 0)
                    {
                        errors.Add("файл пуст");
                    }
                    else if (file.Length > maxBytes)
                    {
                        errors.Add(MediaIntake.TooLargeMessage(maxBytes).TrimEnd('.').ToLowerInvariant());
                    }
                }
            }

            // --- дело ---
            var target = ResolveCase(row, byNumber, errors);

            // --- дата, контрольная сумма, реквизиты ---
            DateTimeOffset? capturedAt = null;
            var rowDateOnly = false;
            if (row.CapturedAt is not null)
            {
                if (!ImportManifest.TryParseCapturedAt(row.CapturedAt, zone, out var parsed, out rowDateOnly))
                {
                    errors.Add($"дата съёмки «{row.CapturedAt}» не разобрана (пример: 14.05.2023 13:45)");
                }
                else if (parsed > nowUtc.AddDays(1))
                {
                    errors.Add("дата съёмки в будущем");
                }
                else
                {
                    capturedAt = parsed;
                }
            }

            string? sha = null;
            if (row.Sha256 is not null)
            {
                sha = ImportManifest.NormalizeSha256(row.Sha256);
                if (sha is null)
                {
                    errors.Add("контрольная сумма SHA-256 — 64 шестнадцатеричных символа");
                }
            }

            var sourceLimit = kind == MediaImportFileKind.Document
                ? MediaImportLimits.DocumentSourceMaxLength
                : UploadMediaValidator.MaxSourceLength;
            if (row.Source is { } sourceText && sourceText.Length > sourceLimit)
            {
                errors.Add($"источник длиннее {sourceLimit} символов");
            }

            // --- реквизиты документа (ADR-0042) ---
            MediaImportDocumentDraft? document = null;
            if (kind == MediaImportFileKind.Document && file is not null)
            {
                document = DocumentRequisites(row, file, capturedAt, zone, typesByName, defaultType, errors);
            }

            if (row.Place is { Length: > UploadMediaValidator.MaxPlaceLength })
            {
                errors.Add($"место длиннее {UploadMediaValidator.MaxPlaceLength} символов");
            }

            if (errors.Count > 0 || file is null || target is null || kind is null || contentType is null)
            {
                problems.Add(new MediaImportProblem(row, string.Join("; ", errors)));
                continue;
            }

            if (kind == MediaImportFileKind.Document)
            {
                documentCount++;
                documentDateFromFile += document!.RegDateFromFile ? 1 : 0;
                capturedAt = null; // у документа дата — реквизит документа, а не дата съёмки
            }
            else
            {
                if (capturedAt is not null && rowDateOnly)
                {
                    dateOnly++;
                }

                if (capturedAt is null && kind is MediaImportFileKind.Image or MediaImportFileKind.Video)
                {
                    fromMetadata++;
                }
            }

            items.Add(new MediaImportItemDraft(
                row.RowNumber,
                file.RelativePath,
                kind.Value,
                contentType,
                file.Length,
                target.Case.CaseId,
                target.Case.Number,
                target.Case.Classification,
                target.Case.DivisionId,
                capturedAt,
                capturedAt is not null && rowDateOnly,
                row.Source,
                row.Place,
                sha,
                document));
        }

        progress?.Report(manifest.Rows.Count);

        var targetById = targets.GroupBy(t => t.Case.CaseId).ToDictionary(g => g.Key, g => g.First());
        var cases = items
            .GroupBy(i => i.CaseId)
            .Select(g => new MediaImportCaseSummary(
                targetById[g.Key], g.Count(), g.Sum(i => i.ByteSize), g.Count(i => i.Kind == MediaImportFileKind.Document)))
            .OrderBy(c => c.Target.Case.Number, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return new MediaImportPlan(
            folder,
            manifestFileName,
            manifest.Sha256,
            items,
            problems,
            cases,
            items.Sum(i => i.ByteSize),
            dateOnly,
            fromMetadata,
            manifest.IgnoredColumns,
            manifest.EncodingName,
            Fingerprint(folder, manifest.Sha256, items, problems.Count),
            documentCount,
            documentDateFromFile);
    }

    // Тип, номер, дата и название документа; ошибки — в общий список строки.
    private static MediaImportDocumentDraft? DocumentRequisites(
        ManifestRow row,
        MediaImportSourceFile file,
        DateTimeOffset? capturedAt,
        TimeZoneInfo zone,
        Dictionary<string, int> typesByName,
        int? defaultType,
        List<string> errors)
    {
        int? typeId = null;
        if (row.DocumentType is { } typeName)
        {
            if (typesByName.TryGetValue(NormalizeName(typeName), out var found))
            {
                typeId = found;
            }
            else
            {
                errors.Add($"тип документа «{typeName}» не найден среди типов для архива (группа «Хранение»)");
            }
        }
        else if (defaultType is { } fallback)
        {
            typeId = fallback;
        }
        else
        {
            errors.Add("укажите тип документа: столбцом «тип документа» в описи или общим для задания");
        }

        if (row.RegNumber is { Length: > MediaImportLimits.DocumentRegNumberMaxLength })
        {
            errors.Add($"рег. номер длиннее {MediaImportLimits.DocumentRegNumberMaxLength} символов");
        }

        if (row.Title is { Length: > MediaImportLimits.DocumentTitleMaxLength })
        {
            errors.Add($"название длиннее {MediaImportLimits.DocumentTitleMaxLength} символов");
        }

        DateOnly? regDate = null;
        var fromFile = false;
        if (row.DocumentDate is { } dateText)
        {
            if (ImportManifest.TryParseCapturedAt(dateText, zone, out var parsed, out _))
            {
                regDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(parsed, zone).DateTime);
            }
            else
            {
                errors.Add($"дата документа «{dateText}» не разобрана (пример: 14.05.2023)");
            }
        }
        else if (capturedAt is { } captured)
        {
            regDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(captured, zone).DateTime);
        }
        else
        {
            regDate = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(
                DateTime.SpecifyKind(file.LastWriteTimeUtc, DateTimeKind.Utc), zone));
            fromFile = true;
        }

        if (typeId is null || regDate is null)
        {
            return null;
        }

        var title = string.IsNullOrWhiteSpace(row.Title) ? Path.GetFileNameWithoutExtension(file.RelativePath) : row.Title.Trim();
        return new MediaImportDocumentDraft(typeId.Value, row.RegNumber?.Trim(), regDate.Value, title, fromFile);
    }

    // Наименование типа: без учёта регистра, лишних пробелов и «ё».
    private static string NormalizeName(string name) =>
        string.Join(' ', name.Trim().ToLowerInvariant().Replace('ё', 'е').Split(' ', StringSplitOptions.RemoveEmptyEntries));

    private static CaseImportTarget? ResolveCase(
        ManifestRow row, Dictionary<string, List<CaseImportTarget>> byNumber, List<string> errors)
    {
        var number = row.CaseNumber?.Trim();
        if (string.IsNullOrEmpty(number))
        {
            errors.Add("не указан номер дела");
            return null;
        }

        if (!byNumber.TryGetValue(number, out var candidates) || candidates.Count == 0)
        {
            errors.Add($"дело № {number} не найдено среди доступных вам");
            return null;
        }

        var code = row.DivisionCode?.Trim();
        if (!string.IsNullOrEmpty(code))
        {
            var matching = candidates
                .Where(c => string.Equals(c.DivisionCode?.Trim(), code, StringComparison.OrdinalIgnoreCase))
                .ToList();
            if (matching.Count == 1)
            {
                return matching[0];
            }

            errors.Add(matching.Count == 0
                ? $"дело № {number} в подразделении с кодом {code} не найдено среди доступных вам"
                : $"дело № {number} в подразделении с кодом {code} не одно — проверьте справочник подразделений");
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        var codes = string.Join(", ", candidates.Select(c => c.DivisionCode ?? "без кода").Distinct(StringComparer.Ordinal));
        errors.Add($"дело № {number} есть в нескольких подразделениях — укажите код подразделения ({codes})");
        return null;
    }

    // Отпечаток того, что подтверждает оператор: папка, опись, по каждому делу — гриф, подразделение и число файлов.
    private static string Fingerprint(
        string folder, string manifestSha, List<MediaImportItemDraft> items, int problemCount)
    {
        var text = new StringBuilder()
            .Append(folder).Append('|').Append(manifestSha).Append('|')
            .Append(items.Count.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(problemCount.ToString(CultureInfo.InvariantCulture)).Append('|')
            .Append(items.Count(i => i.Document is not null).ToString(CultureInfo.InvariantCulture));
        foreach (var group in items.GroupBy(i => (i.CaseId, i.Classification, i.DivisionId)).OrderBy(g => g.Key.CaseId))
        {
            text.Append('|')
                .Append(group.Key.CaseId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(group.Key.Classification.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(group.Key.DivisionId.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(group.Count().ToString(CultureInfo.InvariantCulture));
        }

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
    }
}
