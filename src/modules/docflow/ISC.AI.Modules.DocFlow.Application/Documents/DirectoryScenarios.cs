using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Запросы справочников для форм модуля: подразделения (порт реализует ПРОФИЛЬ — вопрос 3 Э4-35)
/// и пользователи (реестр ядра <c>core.app_user</c> — вопрос 4).
/// </summary>

/// <summary>
/// Пределы регистрации для ТЕКУЩЕГО субъекта: до какого грифа и в каких подразделениях он вправе
/// зарегистрировать документ (ТБ-020/021, этап 6.6).
/// </summary>
/// <param name="MaxClassification">Максимальный гриф субъекта включительно.</param>
/// <param name="OwnerDivisions">
/// Подразделения, которые можно указать ВЛАДЕЛЬЦЕМ документа — пересечение справочника профиля
/// с допуском субъекта. Пустой список означает «регистрировать нельзя», а не «можно любое»
/// (default-deny, ТБ-021).
/// </param>
public sealed record RegistrationScope(short MaxClassification, IReadOnlyList<DivisionItem> OwnerDivisions);

/// <summary>
/// Пределы регистрации текущего субъекта — чтобы форма не предлагала заведомо запрещённое.
/// </summary>
/// <remarks>
/// Это УДОБСТВО, а не защита: серверная проверка в <c>IDocumentStore.CreateAsync</c> остаётся на месте
/// и срабатывает при запросе в обход формы, а также в гонке «допуск отозвали, пока форма была открыта»
/// (значения на странице к моменту отправки уже устарели). Отдельный запрос, а не сужение
/// <see cref="ListDivisionsQuery"/>: карточка документа берёт тем же справочником НАЗВАНИЯ подразделений
/// для показа, и сужение сломало бы отображение назначений в чужие подразделения (§4.1).
/// </remarks>
public sealed record GetRegistrationScopeQuery : IRequest<ResponseDto<RegistrationScope>>
{
    /// <inheritdoc cref="GetRegistrationScopeQuery" />
    public sealed class Handler(IDivisionDirectory directory, IAccessContextProvider accessProvider)
        : IRequestHandler<GetRegistrationScopeQuery, ResponseDto<RegistrationScope>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<RegistrationScope>> Handle(
            GetRegistrationScopeQuery query, CancellationToken cancellationToken)
        {
            // Fail-closed: без контекста допуска GetCurrentAsync бросает — форма не получит НИЧЕГО
            // и не даст выбрать ни гриф, ни подразделение (ТБ-012).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            var allowed = access.AllowedDivisions;
            var divisions = await directory.ListAsync(cancellationToken);

            return ResponseDto<RegistrationScope>.Ok(new RegistrationScope(
                access.MaxClassification,
                [.. divisions.Where(division => allowed.Contains(division.Id))]));
        }
    }
}

/// <summary>Справочник подразделений для выбора (владелец документа, назначения §4.1).</summary>
public sealed record ListDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<DivisionItem>>>
{
    /// <inheritdoc cref="ListDivisionsQuery" />
    public sealed class Handler(IDivisionDirectory directory)
        : IRequestHandler<ListDivisionsQuery, ResponseDto<IReadOnlyList<DivisionItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DivisionItem>>> Handle(
            ListDivisionsQuery query, CancellationToken cancellationToken)
        {
            var items = await directory.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<DivisionItem>>.Ok(items, items.Count);
        }
    }
}

/// <summary>Справочник активных пользователей (инспектор §3.2, исполнитель §4.1).</summary>
public sealed record ListUsersQuery : IRequest<ResponseDto<IReadOnlyList<UserItem>>>
{
    /// <inheritdoc cref="ListUsersQuery" />
    public sealed class Handler(IUserDirectory directory)
        : IRequestHandler<ListUsersQuery, ResponseDto<IReadOnlyList<UserItem>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<UserItem>>> Handle(
            ListUsersQuery query, CancellationToken cancellationToken)
        {
            var items = await directory.ListActiveAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<UserItem>>.Ok(items, items.Count);
        }
    }
}
