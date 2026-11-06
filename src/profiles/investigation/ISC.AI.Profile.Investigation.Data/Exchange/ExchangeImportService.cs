using System.Globalization;
using System.IO.Pipelines;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading.Channels;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Exchange;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Application;
using ISC.AI.Modules.Media.Application.Features.Assets;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Entities;
using ISC.AI.Profile.Investigation.Domain.Exchange;
using ISC.AI.Profile.Investigation.Domain.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Profile.Investigation.Data.Exchange;

/// <summary>
/// Задание на приём пакета. Пароль ключа приёма — массивом, а не строкой: после загрузки ключа он затирается в памяти
/// (ТБ-083); в базу, журнал и логи он не попадает.
/// </summary>
/// <param name="ImportId">Строка журнала приёмов.</param>
/// <param name="PackageName">Каталог пакета на носителе.</param>
/// <param name="UserId">Кто запустил.</param>
/// <param name="Password">Пароль ключа приёма.</param>
public sealed record ExchangeImportJob(int ImportId, string PackageName, int UserId, char[] Password);

/// <summary>Очередь заданий приёма: одна на процесс, читает её <see cref="ExchangeImportWorker"/>.</summary>
public sealed class ExchangeImportChannel
{
    private readonly Channel<ExchangeImportJob> _channel =
        Channel.CreateUnbounded<ExchangeImportJob>(new UnboundedChannelOptions { SingleReader = true });

    /// <summary>Ход текущего приёма словами (для экрана).</summary>
    public string? Stage { get; set; }

    /// <summary>Ставит задание.</summary>
    public ValueTask WriteAsync(ExchangeImportJob job, CancellationToken cancellationToken = default) =>
        _channel.Writer.WriteAsync(job, cancellationToken);

    /// <summary>Читает задания до остановки хоста.</summary>
    public IAsyncEnumerable<ExchangeImportJob> ReadAllAsync(CancellationToken cancellationToken) =>
        _channel.Reader.ReadAllAsync(cancellationToken);
}

/// <summary>
/// Обмен между офисами — сторона получателя (ТФ-ОБМ-02, ADR-0068 v0.2): готовность, пакеты на носителе, запуск и выполнение
/// фонового приёма, акт в журнале аудита, отметка прерванных после перезапуска.
/// </summary>
/// <remarks>
/// <para>
/// ИНВАРИАНТЫ (ТБ-083). (1) До первой записи пакет проверяется целиком: подпись манифеста ключом источника из настройки,
/// состав, объём и SHA-256 каждого файла, отпечаток ключа приёма в манифесте и в каждом файле; содержимое расшифровывается
/// со сверкой SHA-256 и сверяется с манифестом. (2) Файл, подменённый после проверки, не примется: содержимое — по SHA-256
/// манифеста, файлы носителей — по хешу оригинала из подписанного содержимого (сверяет общий приём «Медиа»). (3) Без
/// отметки шлюза приём не начинается. (4) Пароль ключа приёма — только в памяти задания.
/// </para>
/// <para>ИНВАРИАНТ ДОСТУПА (ТБ-079): экран получает только манифест и счётчики; записи пишет задание.</para>
/// </remarks>
public sealed partial class ExchangeImportService(
    IDbContextFactory<InvestigationDbContext> contextFactory,
    ExchangeSettings settings,
    IExchangePackages packages,
    ExchangeImportChannel channel,
    ExchangeRecordImporter records,
    IMediaExchangeSource mediaLookup,
    IMediaStore mediaStore,
    ICaseScope caseScope,
    MediaUploadOptions uploadOptions,
    IBackgroundTaskQueue queue,
    IAuditWriter audit,
    TimeProvider timeProvider,
    ILogger<ExchangeImportService> logger) : IExchangeImport
{
    /// <summary>Вид содержимого, которое принимает этот профиль.</summary>
    public const string AcceptedKind = ExchangePackageData.Kind;

    // Журнал приёмов на экране — последние.
    private const int RecentLimit = 20;

    // Источник носителя — для сотрудников (откуда файл) и для разбора; длина поля носителя.
    private const int SourceTextLimit = 500;

    /// <inheritdoc />
    public async Task<ExchangeImportOverview> GetImportOverviewAsync(CancellationToken cancellationToken = default)
    {
        var readiness = settings.CheckImport(packages);
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var rows = new List<ExchangePackageRow>();
        if (settings.ImportFolder is { } folder)
        {
            var names = packages.ListPackages(folder, ExchangePackageWriter.FolderPrefix);
            var last = (await db.ExchangeImports.AsNoTracking()
                    .Where(i => names.Contains(i.PackageName))
                    .Select(i => new { i.PackageName, i.Status, i.StartedAt })
                    .ToListAsync(cancellationToken))
                .GroupBy(i => i.PackageName, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.OrderByDescending(i => i.StartedAt).First(), StringComparer.Ordinal);
            using var keys = new SourceKeys(settings, packages);
            foreach (var name in names)
            {
                var header = await ReadHeaderAsync(Path.Combine(folder, name), keys, cancellationToken);
                var manifest = header.Manifest;
                var previous = last.GetValueOrDefault(name);
                rows.Add(new ExchangePackageRow(
                    name,
                    manifest?.SourceInstance ?? header.ClaimedSource,
                    manifest?.Period,
                    manifest?.CreatedAt,
                    manifest?.Marking,
                    manifest?.Counts,
                    manifest?.Files.Sum(f => f.Bytes),
                    header.Problem ?? Unacceptable(manifest),
                    previous?.Status,
                    previous?.StartedAt));
            }
        }

        var recent = await db.ExchangeImports.AsNoTracking()
            .OrderByDescending(i => i.StartedAt)
            .Take(RecentLimit)
            .Select(i => new ExchangeImportRow(
                i.Id, i.StartedAt, i.FinishedAt, i.Status, i.StartedByUserId, i.PackageName, i.SourceInstance, i.Period,
                i.GatewayConfirmed, i.CasesCreated, i.PersonsCreated, i.EventsCreated, i.ReportsCreated, i.AssetsCreated,
                i.FilesReceived, i.BytesReceived, i.AlreadyImported, i.Conflicts, i.Deferred, i.MissingFiles, i.Rejected,
                i.Classification, i.Error))
            .ToListAsync(cancellationToken);
        var running = recent.Any(r => r.Status == ExchangeImportStatus.Running) ? channel.Stage ?? "Приём идёт…" : null;
        return new ExchangeImportOverview(readiness, rows, recent, running);
    }

    /// <inheritdoc />
    public async Task<ExchangeImportStartResult> StartImportAsync(
        string packageName, bool gatewayConfirmed, int userId, string receivingKeyPassword, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(receivingKeyPassword);
        if (!gatewayConfirmed)
        {
            return new ExchangeImportStartResult(null, "Отметьте, что пакет прошёл проверку на шлюзе: без неё приём не начинается (ТБ-083).");
        }

        var readiness = settings.CheckImport(packages);
        if (!readiness.Ready)
        {
            return new ExchangeImportStartResult(null, "Приём не настроен: " + string.Join(" ", readiness.Problems));
        }

        // Имя — только из перечня готовых пакетов носителя: ни «../», ни недописанный «.partial» сюда не попадут.
        if (string.IsNullOrWhiteSpace(packageName)
            || !packages.ListPackages(settings.ImportFolder!, ExchangePackageWriter.FolderPrefix).Contains(packageName, StringComparer.Ordinal))
        {
            return new ExchangeImportStartResult(null, $"Пакета «{packageName}» на носителе нет.");
        }

        ExchangePackageHeader header;
        using (var keys = new SourceKeys(settings, packages))
        {
            header = await ReadHeaderAsync(Path.Combine(settings.ImportFolder!, packageName), keys, cancellationToken);
        }

        if ((header.Problem ?? Unacceptable(header.Manifest)) is { } problem)
        {
            return new ExchangeImportStartResult(null, problem);
        }

        // Пароль — сразу: неверный пароль виден на экране, а не через минуту в журнале приёмов.
        try
        {
            using var key = settings.LoadReceivingKey(packages, receivingKeyPassword);
        }
        catch (InvalidOperationException exception)
        {
            return new ExchangeImportStartResult(null, exception.Message);
        }

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        if (await db.ExchangeImports.AnyAsync(i => i.Status == ExchangeImportStatus.Running, cancellationToken))
        {
            return new ExchangeImportStartResult(null, "Пакет уже принимается — дождитесь окончания.");
        }

        var manifest = header.Manifest!;
        var row = new ExchangeImport
        {
            StartedAt = timeProvider.GetUtcNow().UtcDateTime,
            Status = ExchangeImportStatus.Running,
            StartedByUserId = userId,
            PackageName = packageName,
            SourceInstance = manifest.SourceInstance,
            Period = manifest.Period,
            ManifestSha256 = manifest.ManifestSha256,
            GatewayConfirmed = true,
            Classification = manifest.Classification,
        };
        db.ExchangeImports.Add(row);
        await db.SaveChangesAsync(cancellationToken);

        channel.Stage = "Ожидает начала…";
        await channel.WriteAsync(new ExchangeImportJob(row.Id, packageName, userId, receivingKeyPassword.ToCharArray()), cancellationToken);
        return new ExchangeImportStartResult(row.Id, null);
    }

    /// <summary>
    /// Выполняет задание: проверка и расшифровка пакета, перенос записей и файлов, журнал приёмов, акт в журнале аудита.
    /// Пароль затирается в любом исходе.
    /// </summary>
    public async Task RunAsync(ExchangeImportJob job, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(job);
        var tally = new ExchangeImportTally();
        ExchangeManifestInfo? manifest = null;
        try
        {
            channel.Stage = "Проверка пакета: подпись, состав, SHA-256 файлов…";
            var folder = Path.Combine(settings.ImportFolder ?? throw new ExchangeImportRefusedException("Носитель с пакетами не настроен."), job.PackageName);
            using var receivingKey = settings.LoadReceivingKey(packages, job.Password);
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(job.Password.AsSpan()));

            manifest = await VerifyAsync(folder, receivingKey, cancellationToken);
            await UpdateHeaderAsync(job.ImportId, manifest, cancellationToken);

            channel.Stage = "Расшифровка содержимого…";
            var data = await ReadDataAsync(folder, manifest, receivingKey, cancellationToken);

            channel.Stage = "Перенос дел, объектов, сводок и событий связи…";
            var result = await records.ImportAsync(job.ImportId, manifest.SourceInstance, data, tally, cancellationToken);

            await ImportAssetsAsync(job, folder, manifest, data, result, receivingKey, tally, cancellationToken);
            await FinishAsync(job.ImportId, tally, cancellationToken);
            await WriteActAsync(job, manifest, tally, cancellationToken);
            LogImported(logger, job.ImportId, job.PackageName);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            LogImportFailed(logger, exception, job.ImportId);
            var reason = exception is ExchangeImportRefusedException or InvalidOperationException or InvalidDataException
                ? exception.Message
                : "Приём прерван ошибкой: " + exception.Message;
            await FailAsync(job.ImportId, tally, reason, CancellationToken.None);
            await WriteFailureAsync(job, manifest, reason, CancellationToken.None);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(System.Runtime.InteropServices.MemoryMarshal.AsBytes(job.Password.AsSpan()));
            channel.Stage = null;
        }
    }

    /// <summary>
    /// После перезапуска хоста: незавершённые приёмы — «прерван». Принятое до остановки остаётся в карте — повторный приём
    /// того же пакета его пропустит и доведёт остальное.
    /// </summary>
    public async Task<int> RecoverAfterRestartAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        return await db.ExchangeImports.Where(i => i.Status == ExchangeImportStatus.Running).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.Status, ExchangeImportStatus.Interrupted)
            .SetProperty(i => i.FinishedAt, now)
            .SetProperty(i => i.Error, "Хост перезапущен посреди приёма — примите пакет заново: принятое доведётся без дублей."), cancellationToken);
    }

    // ---------- проверка и содержимое ----------

    private async Task<ExchangeManifestInfo> VerifyAsync(string folder, RSA receivingKey, CancellationToken cancellationToken)
    {
        ExchangePackageHeader header;
        RSA? sourceKey;
        using (var keys = new SourceKeys(settings, packages))
        {
            header = await ReadHeaderAsync(folder, keys, cancellationToken);
            if ((header.Problem ?? Unacceptable(header.Manifest)) is { } problem)
            {
                throw new ExchangeImportRefusedException(problem);
            }

            sourceKey = keys.Take(header.Manifest!.SourceInstance);
        }

        using (sourceKey)
        {
            var verification = await packages.VerifyAsync(folder, sourceKey!, packages.Fingerprint(receivingKey), cancellationToken);
            if (!verification.Ok)
            {
                throw new ExchangeImportRefusedException(
                    "Пакет не прошёл проверку: " + string.Join(" ", verification.Problems.Take(10))
                    + (verification.Problems.Count > 10 ? $" … и ещё {(verification.Problems.Count - 10).ToString(CultureInfo.InvariantCulture)}." : string.Empty));
            }

            if (!string.Equals(verification.Manifest!.ManifestSha256, header.Manifest!.ManifestSha256, StringComparison.Ordinal))
            {
                throw new ExchangeImportRefusedException("Манифест пакета изменился после выбора на экране — проверьте носитель и примите пакет заново.");
            }

            return verification.Manifest;
        }
    }

    private async Task<ExchangePackageData> ReadDataAsync(
        string folder, ExchangeManifestInfo manifest, RSA receivingKey, CancellationToken cancellationToken)
    {
        var file = manifest.Files.SingleOrDefault(f => f.Name == ExchangePackageData.FileName)
            ?? throw new ExchangeImportRefusedException("В пакете нет содержимого (data.json.enc).");
        using var plain = new MemoryStream();
        await packages.DecryptAsync(folder, file.Name, plain, receivingKey, file.Sha256, cancellationToken);
        ExchangePackageData data;
        try
        {
            data = JsonSerializer.Deserialize<ExchangePackageData>(plain.ToArray(), ExchangePackageData.Json)
                ?? throw new ExchangeImportRefusedException("Содержимое пакета пусто.");
        }
        catch (JsonException exception)
        {
            throw new ExchangeImportRefusedException("Содержимое пакета не читается: " + exception.Message);
        }

        // Содержимое обязано совпасть с подписанным манифестом — иначе пакет собран с ошибкой или подменён частично.
        if (data.FormatVersion != ExchangePackageData.CurrentFormat)
        {
            throw new ExchangeImportRefusedException(
                $"Версия формата содержимого {data.FormatVersion.ToString(CultureInfo.InvariantCulture)}: эта версия системы читает {ExchangePackageData.CurrentFormat.ToString(CultureInfo.InvariantCulture)}.");
        }

        if (!string.Equals(data.SourceInstance, manifest.SourceInstance, StringComparison.Ordinal))
        {
            throw new ExchangeImportRefusedException(
                $"Содержимое пакета называет источником «{data.SourceInstance}», манифест — «{manifest.SourceInstance}».");
        }

        var mismatched = data.Counts().Where(c => !manifest.Counts.TryGetValue(c.Key, out var value) || value != c.Value).Select(c => c.Key).ToList();
        if (mismatched.Count > 0)
        {
            throw new ExchangeImportRefusedException("Счётчики содержимого не совпали с манифестом: " + string.Join(", ", mismatched) + ".");
        }

        return data;
    }

    private string? Unacceptable(ExchangeManifestInfo? manifest)
    {
        if (manifest is null)
        {
            return null;
        }

        if (!string.Equals(manifest.Kind, AcceptedKind, StringComparison.Ordinal))
        {
            return $"Пакет вида «{manifest.Kind}» этот экземпляр не принимает (ждёт «{AcceptedKind}»).";
        }

        if (string.Equals(manifest.SourceInstance, settings.InstanceId, StringComparison.Ordinal))
        {
            return "Пакет сформирован этим же экземпляром — принимать его здесь нельзя.";
        }

        if (settings.ReceivingKeySha256 is { } pin && !string.Equals(manifest.RecipientKeyFingerprint, pin, StringComparison.OrdinalIgnoreCase))
        {
            return $"Пакет зашифрован для другого ключа ({manifest.RecipientKeyFingerprint}), ключ приёма этого офиса — {pin.ToUpperInvariant()}.";
        }

        return null;
    }

    private async Task<ExchangePackageHeader> ReadHeaderAsync(string folder, SourceKeys keys, CancellationToken cancellationToken)
    {
        try
        {
            return await packages.ReadHeaderAsync(folder, keys.Get, cancellationToken);
        }
        catch (Exception exception) when (exception is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            // Ключ источника не совпал с пином или носитель недоступен — пакет в перечне с причиной, экран не падает.
            return new ExchangePackageHeader(Path.GetFileName(folder), null, null, exception.Message);
        }
    }

    // ---------- носители ----------

    private async Task ImportAssetsAsync(
        ExchangeImportJob job,
        string folder,
        ExchangeManifestInfo manifest,
        ExchangePackageData data,
        ExchangeRecordResult result,
        RSA receivingKey,
        ExchangeImportTally tally,
        CancellationToken cancellationToken)
    {
        var source = manifest.SourceInstance;

        // Носители, принятые из этого же источника, — не конфликт: у источника один файл может лежать в двух носителях
        // (разные подразделения или гриф). Конфликт ТФ-ОБМ-02 — совпадение со своим носителем получателя.
        var fromSource = await records.MappedLocalIdsAsync(source, ExchangeRecordKind.Asset, cancellationToken);
        var local = (await mediaLookup.FindByContentHashAsync([.. data.Assets.Select(a => a.ContentHash)], cancellationToken))
            .Where(m => !fromSource.Contains(m.AssetId))
            .ToLookup(m => m.ContentHash, StringComparer.OrdinalIgnoreCase);
        var intake = new MediaIntake(mediaStore, caseScope, uploadOptions);
        var index = 0;
        foreach (var asset in data.Assets.OrderBy(a => a.SourceId))
        {
            cancellationToken.ThrowIfCancellationRequested();
            index++;
            channel.Stage = $"Носители: {index.ToString(CultureInfo.InvariantCulture)} из {data.Assets.Count.ToString(CultureInfo.InvariantCulture)}…";

            // Дела носителя, принятые у получателя (сейчас или прежде); дела-конфликты ждут Руководителя вместе с носителем.
            var targets = asset.Cases
                .Where(l => result.Cases.ContainsKey(l.CaseSourceId))
                .Select(l => (Link: l, Case: result.Cases[l.CaseSourceId]))
                .ToList();
            if (await records.FindMappedAsync(source, ExchangeRecordKind.Asset, asset.SourceId, cancellationToken) is { } mapped)
            {
                // Принят прежде — привязать к делам, принятым теперь (привязка идемпотентна).
                foreach (var (link, linked) in targets)
                {
                    await caseScope.LinkAssetAsync(linked.LocalId, mapped, link.Place, job.UserId, cancellationToken);
                }

                tally.AlreadyImported++;
                continue;
            }

            if (targets.Count == 0)
            {
                tally.HeldWithConflict++;
                continue;
            }

            // Тот же файл у получателя уже есть — объединять или нет, решает Руководитель (ТФ-ОБМ-02), а не дедупликация.
            if (local[asset.ContentHash].FirstOrDefault() is { } match)
            {
                var label = asset.OriginalFileName;
                await records.AddConflictAsync(job.ImportId, source, ExchangeConflictKind.Asset, asset.SourceId, match.AssetId,
                    "у получателя уже есть носитель с тем же файлом (SHA-256)", label, asset.Classification, targets[0].Case.DivisionId,
                    cancellationToken);
                tally.Conflicts++;
                tally.ConflictLabels.Add("носитель «" + label + "» (тот же файл, что носитель № " + match.AssetId.ToString(CultureInfo.InvariantCulture) + ")");
                continue;
            }

            if (asset.File is null)
            {
                // Файл ушёл прежним пакетом (или его вид не выбирали) — сначала надо принять тот пакет.
                tally.MissingFiles++;
                continue;
            }

            var (firstLink, firstCase) = targets[0];

            // Гриф носителя — не ниже грифа дела и не ниже грифа источника: приём гриф не понижает.
            var target = new CaseScopeItem(
                firstCase.LocalId, firstCase.Number, firstCase.Title, Math.Max(firstCase.Classification, asset.Classification), firstCase.DivisionId);
            var request = new MediaIntakeRequest(
                target,
                asset.OriginalFileName,
                asset.ContentType,
                asset.ByteSize,
                Provenance(source, job.PackageName, asset),
                asset.CapturedAt,
                firstLink.Place,
                job.UserId,
                ExpectedContentHash: asset.ContentHash);
            var received = await ReceiveAsync(intake, request, folder, asset.File, receivingKey, cancellationToken);
            if (!received.Accepted)
            {
                tally.Rejected++;
                tally.Rejections.Add(asset.OriginalFileName + ": " + (received.Message ?? "отказ приёма"));
                continue;
            }

            var assetId = received.Receipt!.AssetId;
            await records.MapAsync(job.ImportId, source, ExchangeRecordKind.Asset, asset.SourceId, assetId, cancellationToken);
            await records.ClearConflictAsync(source, ExchangeConflictKind.Asset, asset.SourceId, cancellationToken);
            foreach (var (link, other) in targets.Skip(1))
            {
                await caseScope.LinkAssetAsync(other.LocalId, assetId, link.Place, job.UserId, cancellationToken);
            }

            if (!received.Receipt.Duplicate)
            {
                // Обработка — тем же путём, что у загрузки: поиск лиц, расшифровка речи (ТФ-МЕД-02/08).
                await MediaProcessing.EnqueueAsync(queue, assetId, received.Kind!.Value, cancellationToken);
            }

            tally.AssetsCreated++;
            tally.FilesReceived++;
            tally.BytesReceived += asset.ByteSize;
        }
    }

    /// <summary>
    /// Файл носителя — расшифровкой прямо в общий приём «Медиа», без открытой копии на диске: приём сверяет сигнатуру,
    /// предел размера и SHA-256 с хешем оригинала из подписанного содержимого пакета.
    /// </summary>
    private async Task<MediaIntakeResult> ReceiveAsync(
        MediaIntake intake, MediaIntakeRequest request, string folder, string fileName, RSA receivingKey, CancellationToken cancellationToken)
    {
        var pipe = new Pipe(new PipeOptions(pauseWriterThreshold: 4 << 20, resumeWriterThreshold: 2 << 20));
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var producer = DecryptIntoAsync(pipe.Writer, folder, fileName, receivingKey, stop.Token);
        MediaIntakeResult result;
        try
        {
            result = await intake.ReceiveAsync(request, pipe.Reader.AsStream(), cancellationToken);
        }
        catch
        {
            await stop.CancelAsync();
            await ObserveAsync(producer);
            throw;
        }

        if (!result.Accepted)
        {
            // Приём отказал, не дочитав (формат, размер): расшифровку дальше не ведём.
            await stop.CancelAsync();
            await ObserveAsync(producer);
            return result;
        }

        await producer;
        return result;
    }

    private async Task DecryptIntoAsync(PipeWriter writer, string folder, string fileName, RSA receivingKey, CancellationToken cancellationToken)
    {
        Exception? failure = null;
        try
        {
            await using var stream = writer.AsStream(leaveOpen: true);
            await packages.DecryptAsync(folder, fileName, stream, receivingKey, expectedSha256: null, cancellationToken);
        }
        catch (Exception exception)
        {
            failure = exception;
            throw;
        }
        finally
        {
            await writer.CompleteAsync(failure);
        }
    }

    private static async Task ObserveAsync(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException or IOException
                                              or CryptographicException or InvalidDataException)
        {
            // Расшифровку остановил отказ приёма или сбой — итог уже известен, второе сообщение не нужно.
        }
    }

    private static string Provenance(string source, string packageName, ExchangeAsset asset)
    {
        var text = $"Принято из {source} (пакет {packageName}, носитель № {asset.SourceId.ToString(CultureInfo.InvariantCulture)})"
            + (string.IsNullOrWhiteSpace(asset.Source) ? string.Empty : "; " + asset.Source.Trim());
        return text.Length <= SourceTextLimit ? text : text[..SourceTextLimit];
    }

    // ---------- журнал и аудит ----------

    private async Task UpdateHeaderAsync(int importId, ExchangeManifestInfo manifest, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await db.ExchangeImports.Where(i => i.Id == importId).ExecuteUpdateAsync(s => s
            .SetProperty(i => i.SourceInstance, manifest.SourceInstance)
            .SetProperty(i => i.Period, manifest.Period)
            .SetProperty(i => i.ManifestSha256, manifest.ManifestSha256)
            .SetProperty(i => i.Classification, manifest.Classification), cancellationToken);
    }

    private async Task FinishAsync(int importId, ExchangeImportTally tally, CancellationToken cancellationToken)
    {
        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        var row = await db.ExchangeImports.SingleAsync(i => i.Id == importId, cancellationToken);
        Apply(row, tally);
        row.Status = ExchangeImportStatus.Succeeded;
        row.FinishedAt = timeProvider.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task FailAsync(int importId, ExchangeImportTally tally, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
            var row = await db.ExchangeImports.SingleAsync(i => i.Id == importId, cancellationToken);
            Apply(row, tally);
            row.Status = ExchangeImportStatus.Failed;
            row.FinishedAt = timeProvider.GetUtcNow().UtcDateTime;
            row.Error = reason.Length <= 2000 ? reason : reason[..2000];
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception failure) when (failure is DbUpdateException or InvalidOperationException or IOException)
        {
            LogFailMarkFailed(logger, failure, importId);
        }
    }

    private static void Apply(ExchangeImport row, ExchangeImportTally tally)
    {
        row.CasesCreated = tally.CasesCreated;
        row.PersonsCreated = tally.PersonsCreated;
        row.EventsCreated = tally.EventsCreated;
        row.ReportsCreated = tally.ReportsCreated;
        row.AssetsCreated = tally.AssetsCreated;
        row.FilesReceived = tally.FilesReceived;
        row.BytesReceived = tally.BytesReceived;
        row.AlreadyImported = tally.AlreadyImported;
        row.Conflicts = tally.Conflicts;
        row.Deferred = tally.Deferred;
        row.MissingFiles = tally.MissingFiles;
        row.Rejected = tally.Rejected;
    }

    // Акт приёма (ТФ-ОБМ-02, ЦС-08): гриф — маркировка пакета; счётчики манифеста и итог по видам — для сверки; перечень
    // конфликтов (номера дел, имена файлов) — в защищённой части (ТБ-032).
    private Task WriteActAsync(ExchangeImportJob job, ExchangeManifestInfo manifest, ExchangeImportTally tally, CancellationToken cancellationToken)
    {
        var counts = string.Join(", ", manifest.Counts.Select(c => c.Key + "=" + c.Value.ToString(CultureInfo.InvariantCulture)));
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"приём пакета {job.PackageName} из {manifest.SourceInstance}: период {manifest.Period}; SHA-256 манифеста {manifest.ManifestSha256}; "
            + $"ключ подписи источника {manifest.SigningKeyFingerprint}; отметка шлюза — да; манифест: {counts}; "
            + $"заведено: дел {tally.CasesCreated}, объектов {tally.PersonsCreated}, реквизитов {tally.RequisitesCreated}, "
            + $"событий связи {tally.EventsCreated}, участников {tally.ParticipantsCreated}, сводок {tally.ReportsCreated}, "
            + $"записей справочника {tally.ReferencesCreated}, носителей {tally.AssetsCreated} (файлов {tally.FilesReceived}, байт {tally.BytesReceived}); "
            + $"уже принято прежде: {tally.AlreadyImported}; конфликтов: {tally.Conflicts}; не перенесено с конфликтными делами: {tally.HeldWithConflict}; "
            + $"отложено до этапа 3 (эталоны, появления): {tally.Deferred}; носителей без файла: {tally.MissingFiles}; отклонено файлов: {tally.Rejected}");
        if (tally.ConflictLabels.Count > 0)
        {
            text += "; конфликты: " + string.Join("; ", tally.ConflictLabels);
        }

        if (tally.Rejections.Count > 0)
        {
            text += "; отклонены: " + string.Join("; ", tally.Rejections);
        }

        return audit.WriteAsync(
            new AuditEntry(
                AuditAction.Ingest,
                manifest.Classification,
                SubjectId: job.UserId,
                ObjectRef: "investigation:exchange:import:" + job.PackageName,
                PayloadSensitive: text),
            cancellationToken);
    }

    private async Task WriteFailureAsync(ExchangeImportJob job, ExchangeManifestInfo? manifest, string reason, CancellationToken cancellationToken)
    {
        try
        {
            await audit.WriteAsync(
                new AuditEntry(
                    AuditAction.Ingest,
                    manifest?.Classification ?? ClassificationLevels.Unclassified,
                    SubjectId: job.UserId,
                    ObjectRef: "investigation:exchange:import:" + job.PackageName,
                    PayloadSensitive: "приём пакета не выполнен: " + (reason.Length <= 500 ? reason : reason[..500]),
                    Outcome: AuditOutcome.Error),
                cancellationToken);
        }
        catch (Exception failure) when (failure is DbUpdateException or InvalidOperationException or IOException)
        {
            LogFailMarkFailed(logger, failure, job.ImportId);
        }
    }

    [LoggerMessage(EventId = 6811, Level = LogLevel.Information, Message = "Пакет обмена принят: приём {ImportId}, {PackageName}")]
    private static partial void LogImported(ILogger logger, int importId, string packageName);

    [LoggerMessage(EventId = 6812, Level = LogLevel.Error, Message = "Пакет обмена не принят: приём {ImportId}")]
    private static partial void LogImportFailed(ILogger logger, Exception exception, int importId);

    [LoggerMessage(EventId = 6813, Level = LogLevel.Error, Message = "Не удалось отметить сбой приёма {ImportId}")]
    private static partial void LogFailMarkFailed(ILogger logger, Exception exception, int importId);

    /// <summary>Ключи подписи источников на время одного обзора или приёма: читаются по требованию, освобождаются разом.</summary>
    private sealed class SourceKeys(ExchangeSettings settings, IExchangePackages packages) : IDisposable
    {
        private readonly Dictionary<string, RSA?> _keys = new(StringComparer.Ordinal);

        public RSA? Get(string instance)
        {
            if (!_keys.TryGetValue(instance, out var key))
            {
                key = settings.LoadSourceKey(packages, instance);
                _keys[instance] = key;
            }

            return key;
        }

        /// <summary>Забирает ключ из набора: дальше им владеет вызывающий.</summary>
        public RSA? Take(string instance)
        {
            var key = Get(instance);
            _keys.Remove(instance);
            return key;
        }

        public void Dispose()
        {
            foreach (var key in _keys.Values)
            {
                key?.Dispose();
            }

            _keys.Clear();
        }
    }
}

/// <summary>
/// Исполнитель заданий приёма (ADR-0068 v0.2): при старте — отметка прерванных приёмов, дальше — задания по одному, каждое в
/// своей области служб. Сбой задания исполнитель не роняет.
/// </summary>
public sealed partial class ExchangeImportWorker(
    ExchangeImportChannel channel, IServiceScopeFactory scopes, ILogger<ExchangeImportWorker> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // База может быть недоступна на старте (хост поднимается и без неё) — не роняем исполнитель, только журнал.
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var interrupted = await scope.ServiceProvider.GetRequiredService<ExchangeImportService>().RecoverAfterRestartAsync(stoppingToken);
            if (interrupted > 0)
            {
                LogRecovered(logger, interrupted);
            }
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogRecoverFailed(logger, exception);
        }

        try
        {
            await foreach (var job in channel.ReadAllAsync(stoppingToken))
            {
                await using var scope = scopes.CreateAsyncScope();
                await scope.ServiceProvider.GetRequiredService<ExchangeImportService>().RunAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Остановка хоста: незавершённый приём будет помечен «прерван» при следующем старте.
        }
    }

    [LoggerMessage(EventId = 6814, Level = LogLevel.Warning, Message = "Прерванные приёмы пакетов обмена помечены: {Count}")]
    private static partial void LogRecovered(ILogger logger, int count);

    [LoggerMessage(EventId = 6815, Level = LogLevel.Error, Message = "Отметка прерванных приёмов пакетов обмена не выполнена")]
    private static partial void LogRecoverFailed(ILogger logger, Exception exception);
}
