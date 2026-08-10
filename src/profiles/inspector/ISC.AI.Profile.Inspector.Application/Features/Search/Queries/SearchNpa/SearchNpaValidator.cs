using FluentValidation;

namespace ISC.AI.Profile.Inspector.Application.Features.Search;

/// <summary>Валидатор поискового запроса: непустой и осмысленной длины.</summary>
public sealed class SearchNpaValidator : AbstractValidator<SearchNpaQuery>
{
    /// <summary>Создаёт правила валидации запроса поиска.</summary>
    public SearchNpaValidator()
    {
        RuleFor(q => q.Query)
            .NotEmpty().WithMessage("Поисковый запрос обязателен.")
            .MinimumLength(2).WithMessage("Запрос слишком короткий.")
            .MaximumLength(500).WithMessage("Запрос слишком длинный.");
    }
}
