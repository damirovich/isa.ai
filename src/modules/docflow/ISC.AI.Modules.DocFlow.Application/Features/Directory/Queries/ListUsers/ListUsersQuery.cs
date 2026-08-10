using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

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
