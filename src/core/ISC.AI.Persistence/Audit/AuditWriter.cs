using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace ISC.AI.Persistence.Audit;

/// <summary>
/// Реализация неизменяемого журнала аудита (ТБ-030/031): только добавление (append-only) со
/// связыванием записей в хеш-цепочку <c>prev_hash → record_hash</c>. Контекст создаётся через
/// <c>IDbContextFactory</c> (ТС-008).
/// </summary>
/// <remarks>
/// Целостность цепочки под конкурентными записями обеспечивается транзакцией и advisory-блокировкой
/// PostgreSQL (сериализация добавления) — иначе две записи могли бы зацепиться за один <c>prev_hash</c>
/// и расщепить цепочку. UPDATE/DELETE не выполняются; запрет модификации дополнительно закреплён
/// триггером БД (миграция) и ролью БД без прав изменения (ТБ-031).
///
/// КОНТЕКСТ ЗАПИСИ (ТБ-080, ADR-0066): IP-адрес клиента и сеанс входа писатель берёт из контекста субъекта
/// (<paramref name="subjectProvider"/>) сам, если сценарий не передал их явно, — ни одно из мест записи не может их
/// «забыть». Без хоста (тесты, программа восстановления) контекста нет — адрес и сеанс пусты.
/// </remarks>
/// <param name="contextFactory">Фабрика контекста ядра.</param>
/// <param name="subjectProvider">Контекст субъекта: адрес клиента и сеанс; <see langword="null"/> — процесс без хоста.</param>
public sealed class AuditWriter(
    IDbContextFactory<CoreDbContext> contextFactory,
    ISubjectProvider? subjectProvider = null) : IAuditWriter
{
    /// <summary>Формула хеша записей до ADR-0066: поля через «|».</summary>
    internal const short LegacyHashVersion = 1;

    /// <summary>Формула хеша новых записей (ADR-0066): исход, адрес и сеанс входят в хеш, каждое поле — с длиной.</summary>
    internal const short CurrentHashVersion = 2;

    // Первая запись цепляется к нулевому хешу (генезис).
    private static readonly byte[] GenesisHash = new byte[32];

    // Произвольный фиксированный ключ advisory-блокировки добавления в журнал (xact-scoped).
    private const long AppendLockKey = 4937042001L;

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entry);

        // Контекст — ДО транзакции: блокировка добавления держится только на время самой вставки.
        var (clientAddress, sessionId) = await ResolveClientContextAsync(entry, cancellationToken);

        await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Сериализуем добавление, чтобы хеш-цепочка не расщеплялась (ТБ-031).
        await db.Database.ExecuteSqlRawAsync($"SELECT pg_advisory_xact_lock({AppendLockKey})", cancellationToken);

        var previousHash = await db.AuditRecords
            .OrderByDescending(r => r.Id)
            .Select(r => r.RecordHash)
            .FirstOrDefaultAsync(cancellationToken) ?? GenesisHash;

        var record = new AuditRecordEntity
        {
            // Усечение до микросекунд: колонка timestamp хранит µs (6 знаков), а DateTime.UtcNow — 100 нс
            // (7 знаков). Хешируем и сохраняем ОДНО И ТО ЖЕ µs-значение — иначе запись хеша по 100-нс биту,
            // которого нет в persisted значении, ломает пересчёт record_hash из БД (ложная «подмена», ТБ-031).
            OccurredAt = TruncateToMicroseconds(DateTime.UtcNow),
            SubjectId = entry.SubjectId,
            Action = entry.Action,
            ObjectRef = entry.ObjectRef,
            Classification = entry.Classification,
            DivisionId = entry.DivisionId,
            PayloadSensitive = entry.PayloadSensitive,
            Outcome = entry.Outcome,
            ClientAddress = clientAddress,
            SessionId = sessionId,
            HashVersion = CurrentHashVersion,
            PrevHash = previousHash,
        };
        record.RecordHash = ComputeRecordHash(previousHash, record);

        db.AuditRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Адрес и сеанс: явные значения сценария важнее контекста субъекта. Сбой определения контекста запись НЕ срывает —
    // запись журнала важнее её подробностей (тот же принцип, что у субъекта в AuditBehavior); отмена — пробрасывается.
    private async Task<(string? ClientAddress, Guid? SessionId)> ResolveClientContextAsync(
        AuditEntry entry, CancellationToken cancellationToken)
    {
        var clientAddress = entry.ClientAddress;
        var sessionId = entry.SessionId;
        if (subjectProvider is not null)
        {
            try
            {
                sessionId ??= await subjectProvider.GetCurrentSessionIdAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                sessionId = null;
            }

            try
            {
                clientAddress ??= await subjectProvider.GetCurrentClientAddressAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                clientAddress = null;
            }
        }

        clientAddress = string.IsNullOrWhiteSpace(clientAddress) ? null : clientAddress.Trim();
        if (clientAddress is { Length: > AuditRecordEntity.MaxClientAddressLength })
        {
            clientAddress = clientAddress[..AuditRecordEntity.MaxClientAddressLength];
        }

        return (clientAddress, sessionId);
    }

    // Микросекундная точность записи времени: 1 µs = 10 тиков (100 нс). Отбрасываем sub-µs остаток,
    // чтобы значение совпадало с тем, что физически хранит PostgreSQL timestamp (µs).
    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Kind);

    /// <summary>
    /// <c>record_hash = SHA-256(prev_hash || канон записи)</c> — по формуле ВЕРСИИ записи (<see cref="AuditRecordEntity.HashVersion"/>):
    /// записи до ADR-0066 проверяются по версии 1, новые — по версии 2 (ТБ-080: прежние записи — по прежней версии).
    /// Подмена любого поля, входящего в канон, рвёт цепочку (ТБ-031).
    /// </summary>
    /// <remarks>
    /// internal: проверка цепочки в тестах пересчитывает хеш из ПРОЧИТАННЫХ из БД полей той же логикой.
    /// Неизвестная версия — исключение: «не знаю, как проверить» не должно читаться как «запись цела».
    /// </remarks>
    internal static byte[] ComputeRecordHash(byte[] previousHash, AuditRecordEntity record)
    {
        ArgumentNullException.ThrowIfNull(previousHash);
        ArgumentNullException.ThrowIfNull(record);

        var canonical = record.HashVersion switch
        {
            LegacyHashVersion => CanonicalV1(record),
            CurrentHashVersion => CanonicalV2(record),
            _ => throw new InvalidOperationException(
                $"Запись журнала № {record.Id.ToString(CultureInfo.InvariantCulture)}: неизвестная версия хеша "
                + $"{record.HashVersion.ToString(CultureInfo.InvariantCulture)} — проверить её целостность нечем."),
        };

        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var buffer = new byte[previousHash.Length + canonicalBytes.Length];
        previousHash.CopyTo(buffer, 0);
        canonicalBytes.CopyTo(buffer, previousHash.Length);

        return SHA256.HashData(buffer);
    }

    // Версия 1 (записи до ADR-0066) — НЕ МЕНЯТЬ: по ней проверяются записи, уже лежащие в журнале.
    private static string CanonicalV1(AuditRecordEntity record) =>
        string.Join('|',
            record.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            record.SubjectId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ((int)record.Action).ToString(CultureInfo.InvariantCulture),
            record.ObjectRef ?? string.Empty,
            record.Classification.ToString(CultureInfo.InvariantCulture),
            record.DivisionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            record.PayloadSensitive ?? string.Empty);

    // Версия 2 (ADR-0066): метка версии и все поля, включая исход, адрес и сеанс. Каждое поле — «<длина>:<значение>»,
    // пустое — «-»: в версии 1 «|» внутри ссылки на объект и сводки позволял сдвинуть границы между полями (переписать
    // гриф и подразделение, перенеся текст между ссылкой и сводкой) без изменения хеша; длина перед полем делает разбор
    // канона однозначным.
    private static string CanonicalV2(AuditRecordEntity record)
    {
        string?[] fields =
        [
            "v" + CurrentHashVersion.ToString(CultureInfo.InvariantCulture),
            record.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            record.SubjectId?.ToString(CultureInfo.InvariantCulture),
            ((int)record.Action).ToString(CultureInfo.InvariantCulture),
            record.ObjectRef,
            record.Classification.ToString(CultureInfo.InvariantCulture),
            record.DivisionId?.ToString(CultureInfo.InvariantCulture),
            record.PayloadSensitive,
            record.Outcome is { } outcome ? ((short)outcome).ToString(CultureInfo.InvariantCulture) : null,
            record.ClientAddress,
            record.SessionId?.ToString("D", CultureInfo.InvariantCulture),
        ];

        var canonical = new StringBuilder();
        foreach (var field in fields)
        {
            canonical.Append(field is null
                ? "-"
                : field.Length.ToString(CultureInfo.InvariantCulture) + ":" + field);
            canonical.Append('|');
        }

        return canonical.ToString();
    }
}
