using FluentValidation;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Правила загрузки носителя (ТС-010, ТФ-МЕД-01): дело, имя, allowlist формата, размер.</summary>
public sealed class UploadMediaValidator : AbstractValidator<UploadMediaCommand>
{
    /// <inheritdoc cref="UploadMediaValidator" />
    public UploadMediaValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(MediaFileRules.MaxFileNameLength);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200)
            .Must(MediaFileRules.IsAllowed)
            .WithMessage("Допустимые форматы носителя: JPEG, PNG, BMP, WebP, TIFF, MP4, WebM, MKV, MOV, AVI.");
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= MediaFileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 200 МБ.");
        RuleFor(c => c.Source).MaximumLength(1000);
        RuleFor(c => c.Place).MaximumLength(1000);
    }
}
