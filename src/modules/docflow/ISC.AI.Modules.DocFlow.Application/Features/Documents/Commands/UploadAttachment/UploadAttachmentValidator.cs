using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class UploadAttachmentValidator : AbstractValidator<UploadAttachmentCommand>
{
    /// <summary>Правила прикрепления сопутствующего файла.</summary>
    public UploadAttachmentValidator()
    {
        RuleFor(c => c.DocumentId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(500);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200);
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= FileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 25 МБ.");
    }
}
