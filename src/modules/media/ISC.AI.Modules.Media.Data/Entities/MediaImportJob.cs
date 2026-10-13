using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Задание пакетного импорта архива (<c>media.import_job</c>, ADR-0041): папка архива, опись, кто запустил, состояние
/// и счётчики. Строки — в <see cref="MediaImportItem"/>. Счётчики ведутся вместе с итогом каждой строки одной
/// транзакцией — сводка не пересчитывает сотни тысяч строк на каждое обновление экрана.
/// </summary>
/// <remarks>
/// Режимных полей у задания нет: оно охватывает дела разных грифов, а само содержит только числа, путь папки и имя
/// описи. Номера дел и пути файлов — в строках, под решёткой (ТБ-020). Пользователь — слабая ссылка на
/// <c>core.app_user</c> (ТО-инф-06), без FK через схемы.
/// </remarks>
public class MediaImportJob : AuditableEntity
{
    /// <summary>Полный путь папки архива, относительно которой заданы пути описи.</summary>
    public required string FolderPath { get; set; }

    /// <summary>Имя файла описи.</summary>
    public required string ManifestFileName { get; set; }

    /// <summary>SHA-256 описи (hex, верхний регистр) — цепочка хранения (ТБ-077).</summary>
    public required string ManifestSha256 { get; set; }

    /// <summary>Кто запустил: от его имени и под его допуском принимается каждый файл.</summary>
    public int OperatorUserId { get; set; }

    /// <summary>Состояние.</summary>
    public MediaImportJobStatus Status { get; set; } = MediaImportJobStatus.Queued;

    /// <summary>Почему остановлено.</summary>
    public string? StopReason { get; set; }

    /// <summary>Когда разобрана последняя строка (UTC).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Строк всего.</summary>
    public int TotalCount { get; set; }

    /// <summary>Принято новых носителей.</summary>
    public int AcceptedCount { get; set; }

    /// <summary>Дубликатов.</summary>
    public int DuplicateCount { get; set; }

    /// <summary>Отказов.</summary>
    public int RejectedCount { get; set; }

    /// <summary>Объём всех строк, байт.</summary>
    public long BytesTotal { get; set; }

    /// <summary>Объём разобранных строк, байт.</summary>
    public long BytesDone { get; set; }

    /// <summary>Строки задания.</summary>
    public List<MediaImportItem> Items { get; set; } = [];
}
