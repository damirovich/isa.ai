using System.Diagnostics;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// DOCX→PDF конвертер поверх внешнего процесса LibreOffice (перенос <c>LibreOfficeDocumentConverter</c>
/// СКИД, §3.3). Путь — <c>DocFlow:Conversion:LibreOfficePath</c>.
/// </summary>
/// <remarks>
/// ЛУЧШЕЕ УСИЛИЕ (см. <see cref="IDocumentConverter"/>): в отличие от СКИД (падал на старте, если
/// LibreOffice не найден) — ОСОЗНАННОЕ отличие для модульной системы: отсутствие внешнего процесса
/// в dev-окружении не должно останавливать весь хост. Отсутствие логируется один раз, дальнейшие
/// вызовы возвращают <see langword="null"/> тихо (не спамят лог на каждой загрузке).
///
/// Эмпирика из СКИД, перенесена дословно: профиль LibreOffice передаётся как <c>file:///</c>
/// (RFC 8089, ТРИ слэша) — на Windows <c>file://</c> (два слэша) не разрешает путь в authority и
/// LibreOffice подвисает 30+ секунд на инициализации без создания PDF. Транзиентные коды выхода
/// 75/77 (блокировка профиля другим процессом) — одна повторная попытка через 500 мс.
/// </remarks>
public sealed class LibreOfficeDocumentConverter : IDocumentConverter
{
    private const int MaxRetries = 1;
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(60);

    private readonly string _libreOfficePath;
    private readonly string _tempRoot;
    private readonly IDocFlowFileStorage _storage;
    private readonly ILogger<LibreOfficeDocumentConverter> _logger;
    private bool _missingLogged;

    /// <summary>Читает путь к LibreOffice и временный каталог конвертации из конфигурации.</summary>
    public LibreOfficeDocumentConverter(
        IConfiguration configuration, IDocFlowFileStorage storage, ILogger<LibreOfficeDocumentConverter> logger)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _storage = storage;
        _logger = logger;
        _libreOfficePath = configuration["DocFlow:Conversion:LibreOfficePath"]
            ?? (OperatingSystem.IsWindows()
                ? @"C:\Program Files\LibreOffice\program\soffice.exe"
                : "/usr/bin/soffice");
        _tempRoot = configuration["DocFlow:Conversion:TempPath"]
            ?? Path.Combine(Path.GetTempPath(), "docflow-conversion");
    }

    /// <inheritdoc />
    public async Task<string?> ConvertToPdfAsync(
        string storedFileName, string category, string subPath, CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_libreOfficePath))
        {
            if (!_missingLogged)
            {
                DocumentConverterLog.LibreOfficeNotFound(_logger, _libreOfficePath);
                _missingLogged = true;
            }

            return null;
        }

        var sessionDir = Path.Combine(_tempRoot, Guid.NewGuid().ToString("N"));
        var profileDir = Path.Combine(sessionDir, "profile");
        Directory.CreateDirectory(sessionDir);
        Directory.CreateDirectory(profileDir);

        try
        {
            var inputPath = Path.Combine(sessionDir, storedFileName);
            await using (var source = await _storage.OpenReadAsync(storedFileName, category, subPath, cancellationToken))
            await using (var destination = File.Create(inputPath))
            {
                await source.CopyToAsync(destination, cancellationToken);
            }

            var succeeded = await RunConversionWithRetryAsync(inputPath, sessionDir, profileDir, cancellationToken);
            if (!succeeded)
            {
                return null;
            }

            var pdfPath = Path.Combine(sessionDir, Path.GetFileNameWithoutExtension(storedFileName) + ".pdf");
            if (!File.Exists(pdfPath))
            {
                DocumentConverterLog.PdfNotProduced(_logger, storedFileName);
                return null;
            }

            await using var pdfStream = File.OpenRead(pdfPath);
            return await _storage.SaveAsync(pdfStream, ".pdf", category, subPath, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            DocumentConverterLog.ConversionFailed(_logger, storedFileName, ex);
            return null;
        }
        finally
        {
            try
            {
                Directory.Delete(sessionDir, recursive: true);
            }
            catch (IOException)
            {
                // Временный файл не удалился — не критично, ОС уберёт TEMP сама; конвертация не откатывается из-за этого.
            }
        }
    }

    private async Task<bool> RunConversionWithRetryAsync(
        string inputPath, string outDir, string profileDir, CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            var exitCode = await RunLibreOfficeOnceAsync(inputPath, outDir, profileDir, cancellationToken);
            if (exitCode == 0)
            {
                return true;
            }

            var isTransient = exitCode is 75 or 77;
            if (isTransient && attempt <= MaxRetries)
            {
                DocumentConverterLog.TransientFailureRetry(_logger, exitCode, attempt);
                await Task.Delay(TimeSpan.FromMilliseconds(500), cancellationToken);
                continue;
            }

            DocumentConverterLog.NonZeroExit(_logger, exitCode);
            return false;
        }
    }

    private async Task<int> RunLibreOfficeOnceAsync(
        string inputPath, string outDir, string profileDir, CancellationToken cancellationToken)
    {
        // file:/// — RFC 8089 (см. remarks класса): на Windows двухслэшевый вариант подвешивает LibreOffice.
        var profileUri = "file:///" + profileDir.Replace('\\', '/');
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = _libreOfficePath,
                Arguments = $"--headless -env:UserInstallation={profileUri} --convert-to pdf --outdir \"{outDir}\" \"{inputPath}\"",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            },
        };

        process.Start();
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(Timeout);

        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            DocumentConverterLog.Timeout(_logger, Timeout.TotalSeconds);
            return -1;
        }

        return process.ExitCode;
    }

    private void TryKill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Процесс уже завершился между таймаутом и Kill — гонка, не ошибка.
        }
    }
}

/// <summary>Строго-типизированные лог-сообщения конвертера (LoggerMessage — CA1848).</summary>
internal static partial class DocumentConverterLog
{
    [LoggerMessage(Level = LogLevel.Warning, Message = "LibreOffice не найден по пути «{Path}» — просмотр DOCX в PDF недоступен (оригинал сохраняется, это не отказ загрузки)")]
    public static partial void LibreOfficeNotFound(ILogger logger, string path);

    [LoggerMessage(Level = LogLevel.Warning, Message = "LibreOffice: транзиентный код выхода {ExitCode}, попытка {Attempt}")]
    public static partial void TransientFailureRetry(ILogger logger, int exitCode, int attempt);

    [LoggerMessage(Level = LogLevel.Error, Message = "LibreOffice завершился с кодом {ExitCode} — PDF-копия не создана")]
    public static partial void NonZeroExit(ILogger logger, int exitCode);

    [LoggerMessage(Level = LogLevel.Error, Message = "LibreOffice отработал, но PDF для «{StoredFileName}» не найден")]
    public static partial void PdfNotProduced(ILogger logger, string storedFileName);

    [LoggerMessage(Level = LogLevel.Error, Message = "LibreOffice: таймаут конвертации ({Seconds}с)")]
    public static partial void Timeout(ILogger logger, double seconds);

    [LoggerMessage(Level = LogLevel.Error, Message = "Конвертация «{StoredFileName}» в PDF не удалась — сохранён только оригинал")]
    public static partial void ConversionFailed(ILogger logger, string storedFileName, Exception exception);
}
