using FluentValidation;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Валидаторы формы сценариев документов (сквозной <c>ValidationBehavior</c> хоста).
/// Правила, требующие БД (тип активен, уникальность рег. номера, Execution-обязательность),
/// проверяет хранилище — валидаторы остаются синхронными и юнит-тестируемыми.
/// </summary>
public sealed class RegisterDocumentValidator : AbstractValidator<RegisterDocumentCommand>
{
    /// <summary>Правила §3.2 + режимные поля (гриф/подразделение — fail-closed).</summary>
    public RegisterDocumentValidator()
    {
        RuleFor(c => c.RegNumber).MaximumLength(200);
        RuleFor(c => c.ShortContent).NotEmpty().WithMessage("Укажите краткое содержание (ТЗ §3.2).")
            .MaximumLength(2000);
        RuleFor(c => c.Source).MaximumLength(500);
        RuleFor(c => c.Notes).MaximumLength(2000);
        RuleFor(c => c.TypeId).GreaterThan(0).WithMessage("Выберите тип документа.");
        RuleFor(c => c.Direction).IsInEnum();
        RuleFor(c => c.Priority).IsInEnum().When(c => c.Priority.HasValue);

        // Решётка доступа (ADR-0017 п.5): без грифа и подразделения документ не регистрируется.
        RuleFor(c => c.Classification).GreaterThanOrEqualTo((short)0)
            .WithMessage("Укажите гриф документа.");
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Укажите подразделение-владельца документа.");

        // ЗАМЕЧАНИЕ: проверка «дата регистрации не в будущем» требует часового пояса эксплуатанта
        // (СКИД: Asia/Bishkek через ITimeProvider) — переносится на этапе 4 вместе с провайдером времени.

        RuleFor(c => c.CommonDeadline).NotNull()
            .When(c => c.UseCommonDeadline)
            .WithMessage("Укажите единый срок исполнения (ТЗ §4.1).");

        RuleForEach(c => c.Assignments).ChildRules(a =>
            a.RuleFor(x => x.DivisionId).GreaterThan(0).WithMessage("Укажите подразделение назначения."));

        RuleFor(c => c.Assignments)
            .Must(list => list.Select(a => a.DivisionId).Distinct().Count() == list.Count)
            .WithMessage("Подразделения назначений не должны повторяться (ТЗ §4.1).");
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class ChangeAssignmentStatusValidator : AbstractValidator<ChangeAssignmentStatusCommand>
{
    /// <summary>Правила формы §4.2.</summary>
    public ChangeAssignmentStatusValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.NewStatus).IsInEnum();
        RuleFor(c => c.Comment).MaximumLength(2000);
    }
}

/// <inheritdoc cref="RegisterDocumentValidator" />
public sealed class ExtendAssignmentDeadlineValidator : AbstractValidator<ExtendAssignmentDeadlineCommand>
{
    /// <summary>Правила формы §4.6 (основание обязательно).</summary>
    public ExtendAssignmentDeadlineValidator()
    {
        RuleFor(c => c.AssignmentId).GreaterThan(0);
        RuleFor(c => c.Reason).NotEmpty().WithMessage("Укажите основание продления (ТЗ §4.6).")
            .MaximumLength(2000);
    }
}
