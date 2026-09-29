using FluentValidation;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Правила формы заведения дела (ТФ-ДЕЛ-01, ТБ-024).</summary>
/// <remarks>
/// Пределы длин — единственный источник и для <see cref="UpdateCaseValidator"/>, и для <c>MaxLength</c>
/// полей страниц (Cases, CaseCard): предел не дублируется числом в UI, иначе ввод проходит на клиенте
/// и отбивается сервером общей ошибкой валидации.
/// </remarks>
public sealed class CreateCaseValidator : AbstractValidator<CreateCaseCommand>
{
    /// <summary>Верхняя граница шкалы грифов — единая шкала платформы (ADR-0030, <see cref="ClassificationLevels"/>).</summary>
    public const short MaxClassification = ClassificationLevels.Max;

    /// <summary>Предел длины номера дела.</summary>
    public const int MaxNumberLength = 100;

    /// <summary>Предел длины названия дела.</summary>
    public const int MaxTitleLength = 500;

    /// <summary>Предел длины основания возбуждения/регистрации.</summary>
    public const int MaxBasisLength = 2000;

    /// <summary>Номер ≤100 непустой; название ≤500; вид и гриф в шкале; подразделение обязательно (без умолчаний, ТБ-024).</summary>
    public CreateCaseValidator()
    {
        RuleFor(c => c.Number).NotEmpty().WithMessage("Укажите номер дела.").MaximumLength(MaxNumberLength);
        RuleFor(c => c.Title).NotEmpty().WithMessage("Укажите название дела.").MaximumLength(MaxTitleLength);
        RuleFor(c => c.Kind).IsInEnum().WithMessage("Неизвестный вид дела.");
        RuleFor(c => c.Classification)
            .InclusiveBetween((short)0, MaxClassification)
            .WithMessage("Гриф дела — от «Без грифа» до «Особой важности».");
        RuleFor(c => c.DivisionId).GreaterThan(0).WithMessage("Укажите подразделение дела (ТБ-024).");
        RuleFor(c => c.InvestigatorUserId).GreaterThan(0).When(c => c.InvestigatorUserId is not null);
        RuleFor(c => c.Basis).MaximumLength(MaxBasisLength);

        // ТФ-ДЕЛ-05: реквизиты задания — ровно у вида «задание по объекту».
        RuleFor(c => c.TaskRequisites).NotNull().When(c => c.Kind == CaseKind.ObjectTask)
            .WithMessage(TaskRequisitesValidator.TaskRequired);
        RuleFor(c => c.TaskRequisites).Null().When(c => c.Kind != CaseKind.ObjectTask)
            .WithMessage(TaskRequisitesValidator.TaskOnlyForTaskKind);
        RuleFor(c => c.TaskRequisites!).SetValidator(new TaskRequisitesValidator()).When(c => c.TaskRequisites is not null);
    }
}
