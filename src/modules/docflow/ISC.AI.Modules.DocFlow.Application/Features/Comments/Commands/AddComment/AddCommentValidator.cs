using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>
/// Правила формы комментария (§4.8). Лимиты файлов НАМЕРЕННО свои, НЕ <c>FileRules</c>: у комментариев
/// в СКИД собственный набор — 10 МБ на файл (не 25), ≤5 файлов (не 10), картинки РАЗРЕШЕНЫ (в отличие
/// от файлов документа/переходов), суммарного лимита нет. Переиспользование <c>FileRules</c> здесь молча
/// подменило бы контракт.
/// </summary>
public sealed class AddCommentValidator : AbstractValidator<AddCommentCommand>
{
    /// <summary>Максимум файлов на комментарий (СКИД §4.8).</summary>
    public const int MaxFiles = 5;

    /// <summary>Максимальный размер файла комментария — 10 МБ (СКИД §4.8, их SAD §5.3).</summary>
    public const long MaxFileBytes = 10L * 1024 * 1024;

    /// <summary>Максимальная длина текста комментария.</summary>
    public const int MaxContentLength = 10_000;

    /// <summary>Разрешённые типы файлов комментария — включая изображения (в отличие от файлов документа).</summary>
    public static readonly IReadOnlySet<string> AllowedContentTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "application/pdf",
        "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        "image/png",
        "image/jpeg",
    };

    /// <summary>Правила добавления комментария.</summary>
    public AddCommentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.Content).NotEmpty().WithMessage("Комментарий не может быть пустым.")
            .MaximumLength(MaxContentLength);
        RuleFor(c => c.CommentType).IsInEnum();
        RuleFor(c => c.Files)
            .Must(files => files is null || files.Count <= MaxFiles)
                .WithMessage($"Не больше {MaxFiles} файлов на комментарий.")
            .Must(files => files is null || files.All(f => f.Content.LongLength is > 0 and <= MaxFileBytes))
                .WithMessage("Файл пуст или больше 10 МБ.")
            .Must(files => files is null || files.All(f => AllowedContentTypes.Contains(f.ContentType)))
                .WithMessage("Допустимые форматы файлов комментария: PDF, DOCX, PNG, JPEG.");
    }
}
