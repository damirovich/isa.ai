using FluentValidation;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Search;

/// <summary>Правила запроса поиска по лицу (ТФ-ПЛ-01/05, ТБ-071): дело и основание, ровно одна проба, границы параметров.</summary>
public sealed class SearchByFaceValidator : AbstractValidator<SearchByFaceQuery>
{
    /// <summary>Предел размера пробного изображения — 25 МБ (проба целиком в памяти до детекции).</summary>
    public const long MaxProbeBytes = 25L * 1024 * 1024;

    /// <inheritdoc cref="SearchByFaceValidator" />
    public SearchByFaceValidator()
    {
        RuleFor(q => q.CaseId).GreaterThan(0);
        RuleFor(q => q.AuthorizationId).GreaterThan(0).WithMessage("Поиск возможен только по основанию дела (ТБ-071).");
        RuleFor(q => q.Scope).IsInEnum();
        RuleFor(q => q.TopK).InclusiveBetween(1, 500).When(q => q.TopK is not null);
        RuleFor(q => q.MaxCosineDistance).InclusiveBetween(0.0, 2.0).When(q => q.MaxCosineDistance is not null);

        // Ровно одна проба: изображение ЛИБО лицо уже проиндексированного носителя (ТФ-ПЛ-03).
        RuleFor(q => q)
            .Must(q => (q.ProbeImage is not null) != (q.ProbeFaceId is not null))
            .WithMessage("Укажите либо пробное изображение, либо лицо носителя — ровно одно.")
            .WithName("Probe");
        RuleFor(q => q.ProbeFaceId).GreaterThan(0).When(q => q.ProbeFaceId is not null);
        RuleFor(q => q.ProbeImage)
            .Must(image => image is { LongLength: > 0 and <= MaxProbeBytes })
            .When(q => q.ProbeImage is not null)
            .WithMessage("Пробное изображение пусто или больше 25 МБ.");
        RuleFor(q => q.ProbeFaceIndex)
            .Must(index => index is null || index >= 0)
            .WithMessage("Номер лица на пробе не может быть отрицательным.");
        RuleFor(q => q.ProbeFaceIndex)
            .Null()
            .When(q => q.ProbeFaceId is not null)
            .WithMessage("Номер лица на пробе задаётся только вместе с пробным изображением.");

        RuleFor(q => q.SelectedCaseIds)
            .Must(ids => ids is { Count: > 0 })
            .When(q => q.Scope == SearchScopeKind.SelectedCases)
            .WithMessage("Для области «выбранные дела» укажите хотя бы одно дело.");
    }
}
