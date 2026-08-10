using ISC.AI.Modules.DocFlow.Domain.Services;
using Microsoft.AspNetCore.Components.Forms;

namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>
/// Чтение выбранных в браузере файлов в <see cref="UploadedFile"/> для отправки командой.
/// </summary>
/// <remarks>
/// Общий помощник трёх секций карточки (файлы, комментарии, диалоги назначений). Лимит на файл —
/// как в валидаторе; итоговую проверку всё равно делает сервер, здесь лимит лишь не даёт затянуть
/// в память заведомо лишнее.
/// </remarks>
public static class BrowserFileReader
{
    /// <summary>Максимальный размер одного файла (25 МБ) — согласован с <c>FileRules</c>.</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Читает байты браузерных файлов.</summary>
    public static async Task<List<UploadedFile>> ReadAsync(IEnumerable<IBrowserFile> files)
    {
        ArgumentNullException.ThrowIfNull(files);

        var result = new List<UploadedFile>();
        foreach (var file in files)
        {
            using var buffer = new MemoryStream();
            await using (var stream = file.OpenReadStream(MaxFileBytes))
            {
                await stream.CopyToAsync(buffer);
            }

            result.Add(new UploadedFile(file.Name, file.ContentType, buffer.ToArray()));
        }

        return result;
    }
}
