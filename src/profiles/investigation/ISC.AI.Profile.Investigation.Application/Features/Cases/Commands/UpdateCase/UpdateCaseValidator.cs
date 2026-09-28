using FluentValidation;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Правила формы правки дела (пределы длин — общие с <see cref="CreateCaseValidator"/>).</summary>
public sealed class UpdateCaseValidator : AbstractValidator<UpdateCaseCommand>
{
    /// <summary>Идентификатор положительный; название ≤500 непустое; вид в перечислении.</summary>
    public UpdateCaseValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.Title).NotEmpty().WithMessage("Укажите название дела.").MaximumLength(CreateCaseValidator.MaxTitleLength);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид дела.");
        RuleFor(c => c.InvestigatorUserId).GreaterThan(0).When(c => c.InvestigatorUserId is not null);
        RuleFor(c => c.Basis).MaximumLength(CreateCaseValidator.MaxBasisLength);

        // ТФ-ДЕЛ-05: те же правила, что при заведении, — реквизиты задания ровно у задания.
        RuleFor(c => c.TaskRequisites).NotNull().When(c => c.Kind == CaseKind.ObjectTask)
            .WithMessage(TaskRequisitesValidator.TaskRequired);
        RuleFor(c => c.TaskRequisites).Null().When(c => c.Kind != CaseKind.ObjectTask)
            .WithMessage(TaskRequisitesValidator.TaskOnlyForTaskKind);
        RuleFor(c => c.TaskRequisites!).SetValidator(new TaskRequisitesValidator()).When(c => c.TaskRequisites is not null);
    }
}
