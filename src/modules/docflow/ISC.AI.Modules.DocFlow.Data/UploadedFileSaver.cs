using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Data;

/// <summary>
/// Общий приём «файлы операции»: содержимое — в защищённое хранилище ДО SaveChanges, при сбое БД —
/// компенсирующее удаление сохранённого. Раньше блок повторялся почти дословно в трёх местах
/// (переход статуса §4.2, продление срока §4.6, комментарий §4.8) — код надёжности расходиться
/// не должен. Какую строку метаданных создать, решает вызывающий через <c>addMetadataRow</c>:
/// у каждой операции своя дочерняя сущность (StatusHistoryFile / DeadlineExtensionFile /
/// DocumentCommentFile).
/// </summary>
internal static class UploadedFileSaver
{
    /// <summary>
    /// Сохраняет файлы в хранилище и регистрирует метаданные. Имена ДОПИСЫВАЮТСЯ в
    /// <paramref name="savedFileNames"/> по одному: если сбой случится на середине списка,
    /// вызывающий обязан компенсировать уже сохранённое (<see cref="CleanupAsync"/>).
    /// </summary>
    public static async Task SaveAsync(
        IDocFlowFileStorage storage,
        IEnumerable<UploadedFile>? files,
        string category,
        string subPath,
        List<string> savedFileNames,
        Action<UploadedFile, string> addMetadataRow,
        CancellationToken cancellationToken)
    {
        foreach (var file in files ?? [])
        {
            using var content = new MemoryStream(file.Content);
            var storedFileName = await storage.SaveAsync(
                content, Path.GetExtension(file.FileName), category, subPath, cancellationToken);
            savedFileNames.Add(storedFileName);
            addMetadataRow(file, storedFileName);
        }
    }

    /// <summary>
    /// Компенсирующее удаление после сбоя БД: строк метаданных уже не будет, и файлы без них —
    /// невидимый мусор в хранилище. Токен не передаётся намеренно — уборку нельзя отменить отменой
    /// исходной операции.
    /// </summary>
    public static async Task CleanupAsync(
        IDocFlowFileStorage storage, IReadOnlyList<string> savedFileNames, string category, string subPath)
    {
        foreach (var storedFileName in savedFileNames)
        {
            await storage.DeleteAsync(storedFileName, category, subPath, CancellationToken.None);
        }
    }
}
