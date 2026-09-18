using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Гарантированное удаление носителя и ВСЕХ производных — кадров, лиц, шаблонов, файлов исходника и
/// вырезок (ТБ-064 применительно к биометрии, ТБ-075, GATE-6). Реализация — <c>Media.Data</c>.
/// </summary>
/// <remarks>
/// FAIL-CLOSED порядок (как у <c>DocumentPurger</c> ядра): запись в неизменяемый аудит идёт ПЕРВОЙ;
/// недоступен журнал — удаление не выполняется. Строки БД снимаются одной транзакцией (каскад FK
/// внутри схемы <c>media</c>), файлы — после фиксации транзакции (файл без строки безвреден и
/// подбирается сборщиком; строка без файла — нет).
/// </remarks>
public interface IMediaPurger
{
    /// <summary>Удаляет носитель по идентификатору; отсутствующий — идемпотентный no-op без аудита.</summary>
    Task<MediaPurgeResult> PurgeAsync(int assetId, int? subjectId = null, CancellationToken cancellationToken = default);
}
