using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Контекст поиска по лицу для страницы поиска (ТБ-071, ТФ-ПЛ-05): основания текущего дела (без основания
/// поиск технически невозможен) и перечень доступных субъекту дел для выбора области «все доступные дела».
/// </summary>
/// <param name="Authorizations">Основания поиска дела <see cref="GetCaseSearchScopeQuery.CaseId"/>.</param>
/// <param name="AccessibleCases">Дела, доступные субъекту (область поиска).</param>
public sealed record CaseSearchScope(
    IReadOnlyList<CaseAuthorizationItem> Authorizations,
    IReadOnlyList<CaseScopeItem> AccessibleCases)
{
    /// <summary>Можно ли вообще запускать поиск в контексте этого дела: есть хотя бы одно основание (ТБ-071).</summary>
    public bool CanSearch => Authorizations.Count > 0;
}

/// <summary>Границы поиска по лицу в контексте дела (для страницы поиска).</summary>
/// <remarks>
/// Это УДОБСТВО формы, а не защита: модуль «Медиа» при запуске поиска повторно проверяет дело и основание
/// через <see cref="ICaseScope"/> (ТБ-071) — обход формы ничего не даёт.
/// </remarks>
public sealed record GetCaseSearchScopeQuery(int CaseId) : IRequest<ResponseDto<CaseSearchScope>>
{
    /// <inheritdoc cref="GetCaseSearchScopeQuery" />
    public sealed class Handler(ICaseScope caseScope, IAccessContextProvider accessProvider)
        : IRequestHandler<GetCaseSearchScopeQuery, ResponseDto<CaseSearchScope>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseSearchScope>> Handle(
            GetCaseSearchScopeQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): нет допуска — нет и контекста поиска.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // Неразличимость (ТБ-020): недоступное и несуществующее дело — один ответ.
            if (await caseScope.GetCaseAsync(query.CaseId, access, cancellationToken) is null)
            {
                return ResponseDto<CaseSearchScope>.NotFound("Дело не найдено или недоступно.");
            }

            var authorizations = await caseScope.ListAuthorizationsAsync(query.CaseId, access, cancellationToken);
            var accessible = await caseScope.ListAccessibleCasesAsync(access, cancellationToken);
            return ResponseDto<CaseSearchScope>.Ok(new CaseSearchScope(authorizations, accessible));
        }
    }
}
