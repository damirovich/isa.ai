using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>Общие файловые лимиты (перенос контракта СКИД §6.1: защита от падения на MaxRequestBodySize).</summary>
internal static class FileRules
{
    /// <summary>Максимум файлов на одну операцию.</summary>
    public const int MaxCount = 10;

    /// <summary>Максимальный размер одного файла (25 МБ).</summary>
    public const long MaxFileBytes = 25L * 1024 * 1024;

    /// <summary>Максимальный суммарный размер файлов операции (50 МБ).</summary>
    public const long MaxTotalBytes = 50L * 1024 * 1024;

    /// <summary>
    /// Перенос allowlist СКИД (<c>UploadDocumentFileCommandValidator</c>/<c>UploadedFileValidator</c>):
    /// файл документа и файлы к переходам/продлениям — только PDF/DOCX/DOC. Сопутствующие вложения
    /// (<see cref="UploadAttachmentValidator"/>) — БЕЗ ограничения, как и в СКИД; туда же естественно
    /// ложатся картинки для предпросмотра (этап 4.3) — новый тип для загрузки не заводился.
    /// </summary>
    public static readonly IReadOnlySet<string> AllowedDocumentContentTypes = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "application/msword",
    };

    /// <summary>Подключает правила списка файлов к валидатору команды.</summary>
    public static void ApplyFileListRules<T>(
        this AbstractValidator<T> validator,
        System.Linq.Expressions.Expression<Func<T, IReadOnlyList<Domain.Services.UploadedFile>?>> files)
    {
        validator.RuleFor(files)
            .Must(list => list is null || list.Count <= MaxCount)
                .WithMessage($"Не больше {MaxCount} файлов за одну операцию.")
            .Must(list => list is null || list.All(f => f.Content.LongLength <= MaxFileBytes))
                .WithMessage("Файл больше 25 МБ.")
            .Must(list => list is null || list.Sum(f => f.Content.LongLength) <= MaxTotalBytes)
                .WithMessage("Суммарный размер файлов больше 50 МБ.")
            .Must(list => list is null || list.All(f => AllowedDocumentContentTypes.Contains(f.ContentType)))
                .WithMessage("Допустимые форматы: PDF, DOCX, DOC.");
    }
}
