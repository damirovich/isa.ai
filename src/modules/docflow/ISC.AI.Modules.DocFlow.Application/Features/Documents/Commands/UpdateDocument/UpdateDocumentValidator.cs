using FluentValidation;
using ISC.AI.Modules.DocFlow.Domain.Services;

namespace ISC.AI.Modules.DocFlow.Application.Features.Documents;

/// <summary>
/// Валидатор правки документа (§3.2). Правила формы те же, что при регистрации, — реквизиты одни и те же.
/// </summary>
/// <remarks>
/// Отдельный класс, а не общий базовый с <see cref="RegisterDocumentValidator"/>: у регистрации есть
/// назначения и единый срок, у правки их нет, и общий предок пришлось бы обвешивать условиями
/// «а это только при создании» — ровно та связанность, из-за которой потом ломается и то и другое.
/// Правила, требующие БД (тип активен, уникальность номера, смена группы, режимные ограничения по
/// грифу), проверяет хранилище — здесь только форма.
/// </remarks>
public sealed class UpdateDocumentValidator : AbstractValidator<UpdateDocumentCommand>
{
    /// <inheritdoc cref="UpdateDocumentValidator" />
    public UpdateDocumentValidator(IDocFlowClock clock)
    {
        ArgumentNullException.ThrowIfNull(clock);

        RuleFor(c => c.DocumentId).GreaterThan(0);

        RuleFor(c => c.RegDate).Must(d => d <= clock.Today)
            .WithMessage("Дата регистрации не может быть в будущем.");

        RuleFor(c => c.RegNumber).MaximumLength(200);
        RuleFor(c => c.ShortContent).NotEmpty().WithMessage("Укажите краткое содержание (ТЗ §3.2).")
            .MaximumLength(2000);
        RuleFor(c => c.Source).MaximumLength(500);
        RuleFor(c => c.Notes).MaximumLength(2000);
        RuleFor(c => c.TypeId).GreaterThan(0).WithMessage("Выберите тип документа.");
        RuleFor(c => c.Direction).IsInEnum();
        RuleFor(c => c.Priority).IsInEnum().When(c => c.Priority.HasValue);

        RuleFor(c => c.Classification).GreaterThanOrEqualTo((short)0)
            .WithMessage("Укажите гриф документа.");
        RuleFor(c => c.DivisionId).GreaterThan(0)
            .WithMessage("Укажите подразделение-владельца документа.");
    }
}
