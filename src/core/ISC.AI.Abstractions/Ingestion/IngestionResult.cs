namespace ISC.AI.Abstractions.Ingestion;

/// <summary>Результат загрузки документа (ТБ-024, ТНД-002).</summary>
/// <param name="Accepted">Принят ли документ к индексации.</param>
/// <param name="DocumentId">Идентификатор документа (при приёме или дубликате).</param>
/// <param name="ChunkCount">Сколько чанков проиндексировано (0 — дубликат: уже загружен).</param>
/// <param name="RejectionReason">Причина отказа (если <see cref="Accepted"/> = <see langword="false"/>).</param>
public sealed record IngestionResult(bool Accepted, int? DocumentId, int ChunkCount, string? RejectionReason)
{
    /// <summary>Отказ (fail-closed): документ не проиндексирован.</summary>
    public static IngestionResult Reject(string reason) => new(false, null, 0, reason);

    /// <summary>Успешная индексация документа с <paramref name="chunkCount"/> чанками.</summary>
    public static IngestionResult Ok(int documentId, int chunkCount) => new(true, documentId, chunkCount, null);

    /// <summary>Идемпотентный повтор (ТНД-002): документ с тем же содержимым уже загружен.</summary>
    public static IngestionResult Duplicate(int documentId) => new(true, documentId, 0, null);
}
