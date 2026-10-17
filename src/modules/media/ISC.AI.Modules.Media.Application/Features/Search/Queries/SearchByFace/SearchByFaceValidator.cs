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
        RuleFor(q => q.MinSimilarityPercent).InclusiveBetween(1, 100).When(q => q.MinSimilarityPercent is not null)
            .WithMessage("Порог схожести — от 1 до 100 %.");

        // Ровно одна проба: изображение, лицо уже проиндексированного носителя (ТФ-ПЛ-03) ЛИБО эталоны фигуранта (ADR-0046).
        RuleFor(q => q)
            .Must(q => (q.ProbeImage is not null ? 1 : 0) + (q.ProbeFaceId is not null ? 1 : 0) + (q.ProbeFaceIds is not null ? 1 : 0) == 1)
            .WithMessage("Укажите ровно одну пробу: пробное изображение, лицо носителя или эталоны фигуранта.")
            .WithName("Probe");
        RuleFor(q => q.ProbeFaceId).GreaterThan(0).When(q => q.ProbeFaceId is not null);
        RuleFor(q => q.ProbeFaceIds)
            .Must(ids => ids!.Count is >= 2 and <= SearchByFaceQuery.MaxProbeFaces && ids.All(id => id > 0) && ids.Distinct().Count() == ids.Count)
            .When(q => q.ProbeFaceIds is not null)
            .WithMessage($"Эталонов в одном поиске — от 2 до {SearchByFaceQuery.MaxProbeFaces}, без повторов.");
        RuleFor(q => q.ProbeImage)
            .Must(image => image is { LongLength: > 0 and <= MaxProbeBytes })
            .When(q => q.ProbeImage is not null)
            .WithMessage("Пробное изображение пусто или больше 25 МБ.");
        RuleFor(q => q.ProbeFaceIndex)
            .Must(index => index is null || index >= 0)
            .WithMessage("Номер лица на пробе не может быть отрицательным.");
        RuleFor(q => q.ProbeFaceIndex)
            .Null()
            .When(q => q.ProbeFaceId is not null || q.ProbeFaceIds is not null)
            .WithMessage("Номер лица на пробе задаётся только вместе с пробным изображением.");

        RuleFor(q => q.SelectedCaseIds)
            .Must(ids => ids is { Count: > 0 })
            .When(q => q.Scope == SearchScopeKind.SelectedCases)
            .WithMessage("Для области «выбранные дела» укажите хотя бы одно дело.");
    }
}
