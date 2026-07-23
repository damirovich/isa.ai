using System.Globalization;
using System.Security.Cryptography;
using System.Text;

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
/// </remarks>
public sealed class AuditWriter(IDbContextFactory<CoreDbContext> contextFactory) : IAuditWriter
{
    // Первая запись цепляется к нулевому хешу (генезис).
    private static readonly byte[] GenesisHash = new byte[32];

    // Произвольный фиксированный ключ advisory-блокировки добавления в журнал (xact-scoped).
    private const long AppendLockKey = 4937042001L;

    /// <inheritdoc />
    public async Task WriteAsync(AuditEntry entry, CancellationToken cancellationToken = default)
    {
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
            PrevHash = previousHash,
        };
        record.RecordHash = ComputeRecordHash(previousHash, record);

        db.AuditRecords.Add(record);
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    // Микросекундная точность записи времени: 1 µs = 10 тиков (100 нс). Отбрасываем sub-µs остаток,
    // чтобы значение совпадало с тем, что физически хранит PostgreSQL timestamp (µs).
    private static DateTime TruncateToMicroseconds(DateTime value) =>
        new(value.Ticks - (value.Ticks % TimeSpan.TicksPerMicrosecond), value.Kind);

    // record_hash = SHA-256(prev_hash || канонические поля записи). Подмена любого поля рвёт цепочку.
    // internal: тест обнаружения подмены пересчитывает хеш из ПРОЧИТАННЫХ из БД полей той же логикой.
    internal static byte[] ComputeRecordHash(byte[] previousHash, AuditRecordEntity record)
    {
        var canonical = string.Join('|',
            record.OccurredAt.ToString("O", CultureInfo.InvariantCulture),
            record.SubjectId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            ((int)record.Action).ToString(CultureInfo.InvariantCulture),
            record.ObjectRef ?? string.Empty,
            record.Classification.ToString(CultureInfo.InvariantCulture),
            record.DivisionId?.ToString(CultureInfo.InvariantCulture) ?? string.Empty,
            record.PayloadSensitive ?? string.Empty);

        var canonicalBytes = Encoding.UTF8.GetBytes(canonical);
        var buffer = new byte[previousHash.Length + canonicalBytes.Length];
        previousHash.CopyTo(buffer, 0);
        canonicalBytes.CopyTo(buffer, previousHash.Length);

        return SHA256.HashData(buffer);
    }
}
