using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Что нужно карточке фигуранта для кнопки «Искать по материалам».</summary>
/// <param name="CanSearch">Право «Поиск по лицу» матрицы доступа открыто.</param>
/// <param name="Authorizations">Основания поиска дела фигуранта — те же, что покажет страница поиска (ТБ-071).</param>
public sealed record PersonSearchContext(bool CanSearch, IReadOnlyList<CaseAuthorizationItem> Authorizations);

/// <summary>
/// Готовность к поиску по фигуранту одной кнопкой (ТФ-ПЛ-01/03): право и основания дела. Это подсказка интерфейсу —
/// сам поиск идёт обычным сценарием модуля «Медиа» с проверкой права, основания и полным аудитом (ТБ-071/072).
/// </summary>
/// <param name="CaseId">Дело фигуранта.</param>
public sealed record GetPersonSearchContextQuery(int CaseId) : IRequest<ResponseDto<PersonSearchContext>>
{
    /// <inheritdoc cref="GetPersonSearchContextQuery" />
    public sealed class Handler(
        IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider, ICaseScope caseScope)
        : IRequestHandler<GetPersonSearchContextQuery, ResponseDto<PersonSearchContext>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<PersonSearchContext>> Handle(GetPersonSearchContextQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            var canSearch = await RoleGuard.CallerHasAsync(roles, subjectProvider, InvestigationPermissions.MediaSearch, cancellationToken);
            if (!canSearch)
            {
                // Без права основания не нужны — и не читаются.
                return ResponseDto<PersonSearchContext>.Ok(new PersonSearchContext(false, []));
            }

            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var authorizations = await caseScope.ListAuthorizationsAsync(query.CaseId, access, cancellationToken);
            return ResponseDto<PersonSearchContext>.Ok(new PersonSearchContext(true, authorizations));
        }
    }
}
