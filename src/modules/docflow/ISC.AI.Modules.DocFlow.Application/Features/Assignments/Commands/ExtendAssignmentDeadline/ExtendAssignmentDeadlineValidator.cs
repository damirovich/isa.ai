using FluentValidation;
using ISC.AI.Modules.DocFlow.Application.Features.Documents;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Assignments;

public sealed class ExtendAssignmentDeadlineValidator : AbstractValidator<ExtendAssignmentDeadlineCommand>
{
    /// <summary>Правила формы §4.6 (основание обязательно; срок строго в будущем; + файловые лимиты).</summary>
    public ExtendAssignmentDeadlineValidator(IDocFlowClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.Reason).NotEmpty().WithMessage("Укажите основание продления (ТЗ §4.6).")
            .MaximumLength(2000);

        // Перенос СКИД (ExtendDeadlineCommandValidator, Contracts §6.2): продление СТРОГО в будущее —
        // «сегодня» и раньше не продление, а искажение истории (следующий тик снова пометит просроченным).
        RuleFor(c => c.NewDeadline).Must(d => d > clock.Today)
            .WithMessage("Новый срок должен быть строго позже сегодняшней даты.");

        this.ApplyFileListRules(c => c.Files);
    }
}
