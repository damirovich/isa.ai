using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Editor;

/// <summary>Пределы ИИ-правки: текст и команда обязательны; простыня сверх окна модели отсекается раньше.</summary>
public sealed class ReviseDocumentValidator : AbstractValidator<ReviseDocumentCommand>
{
    /// <inheritdoc cref="ReviseDocumentValidator" />
    public ReviseDocumentValidator()
    {
        RuleFor(c => c.Text).NotEmpty().WithMessage("Вставьте текст документа.")
            .MaximumLength(100_000);
        RuleFor(c => c.Instruction).NotEmpty().WithMessage("Укажите команду правки.")
            .MaximumLength(500);
    }
}
