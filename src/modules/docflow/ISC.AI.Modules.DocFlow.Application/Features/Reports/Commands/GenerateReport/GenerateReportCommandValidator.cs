using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;
using System.Globalization;

namespace ISC.AI.Modules.DocFlow.Application.Features.Reports;

/// <summary>Валидатор отбора отчёта: период обязателен и ограничен по длине.</summary>
public sealed class GenerateReportCommandValidator : AbstractValidator<GenerateReportCommand>
{
    /// <summary>
    /// Предельная длина периода. Отчёт грузится в память целиком и рендерится в файл; без потолка
    /// один запрос «с 1900 года» кладёт процесс, а пользы в такой выгрузке нет — она нечитаема.
    /// </summary>
    public const int MaxPeriodDays = 1100;

    /// <inheritdoc cref="GenerateReportCommandValidator" />
    public GenerateReportCommandValidator()
    {
        RuleFor(command => command.Filter).NotNull();

        RuleFor(command => command.Filter.To)
            .GreaterThanOrEqualTo(command => command.Filter.From)
            .WithMessage("Конец периода не может быть раньше начала.");

        RuleFor(command => command.Filter)
            .Must(filter => filter is null || filter.To.DayNumber - filter.From.DayNumber <= MaxPeriodDays)
            .WithMessage($"Период отчёта не может превышать {MaxPeriodDays} дней.");

        RuleFor(command => command.Kind).IsInEnum();
        RuleFor(command => command.Format).IsInEnum();
    }
}
