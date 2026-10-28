using ISC.AI.Abstractions.Backup;

namespace ISC.AI.Persistence.Entities;

/// <summary>
/// Прогон резервной копии (<c>core.backup_run</c>, ТФ-АДМ-04, ADR-0063): кто и когда запустил, что вошло, итог и
/// последняя проверка манифеста. Строки не удаляются — это история копий для Администратора и проверяющих.
/// </summary>
public class BackupRunEntity
{
    /// <summary>Наибольшая длина причины сбоя и пояснения проверки.</summary>
    public const int MaxNoteLength = 2000;

    /// <summary>Наибольшая длина имени каталога копии.</summary>
    public const int MaxFolderLength = 100;

    public int Id { get; set; }

    public DateTime StartedAt { get; set; }

    public DateTime? FinishedAt { get; set; }

    public BackupRunStatus Status { get; set; }

    public int? StartedByUserId { get; set; }

    public string? FolderName { get; set; }

    public int Databases { get; set; }

    public int? StorageFiles { get; set; }

    public long? StorageBytes { get; set; }

    public long BytesWritten { get; set; }

    public string? Error { get; set; }

    public BackupVerifyStatus? VerifyStatus { get; set; }

    public DateTime? VerifiedAt { get; set; }

    public int? VerifiedByUserId { get; set; }

    public string? VerifyNote { get; set; }
}
