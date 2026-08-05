using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Documents;

/// <summary>
/// Запросы справочников для форм модуля: подразделения (порт реализует ПРОФИЛЬ — вопрос 3 Э4-35)
/// и пользователи (реестр ядра <c>core.app_user</c> — вопрос 4).
/// </summary>

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
