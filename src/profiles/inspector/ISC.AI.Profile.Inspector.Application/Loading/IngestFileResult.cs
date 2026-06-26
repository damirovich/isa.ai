namespace ISC.AI.Profile.Inspector.Application.Loading;

/// <summary>Итог загрузки одного файла в корпус (Э4-01).</summary>
/// <param name="FileName">Имя файла.</param>
/// <param name="Accepted">Принят ли (или дубликат).</param>
/// <param name="DocumentId">Идентификатор документа в корпусе (при приёме/дубликате).</param>
/// <param name="ChunkCount">Сколько чанков проиндексировано (0 — дубликат).</param>
/// <param name="Reason">Причина отказа, если не принят.</param>
/// <param name="IsDuplicate">Идемпотентный повтор (уже в корпусе).</param>
public sealed record IngestFileResult(
    string FileName, bool Accepted, int? DocumentId, int ChunkCount, string? Reason, bool IsDuplicate);
