namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

/// <summary>Результат экспорта справки: готовый файл для скачивания.</summary>
/// <param name="Content">Байты файла <c>.docx</c>.</param>
/// <param name="FileName">Имя файла для скачивания.</param>
/// <param name="ContentType">MIME-тип (<c>.docx</c>).</param>
public sealed record ExportReferenceResult(byte[] Content, string FileName, string ContentType);
