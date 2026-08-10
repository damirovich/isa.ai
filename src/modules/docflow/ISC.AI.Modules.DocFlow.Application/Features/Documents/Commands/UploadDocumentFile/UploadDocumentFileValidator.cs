using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class UploadDocumentFileValidator : AbstractValidator<UploadDocumentFileCommand>
{
    /// <summary>Правила загрузки файла документа (§3.3).</summary>
    public UploadDocumentFileValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(500);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200)
            .Must(ct => FileRules.AllowedDocumentContentTypes.Contains(ct))
            .WithMessage("Допустимые форматы файла документа: PDF, DOCX, DOC (ТЗ СКИД).");
        RuleFor(c => c.Language).IsInEnum();
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= FileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 25 МБ.");
    }
}
