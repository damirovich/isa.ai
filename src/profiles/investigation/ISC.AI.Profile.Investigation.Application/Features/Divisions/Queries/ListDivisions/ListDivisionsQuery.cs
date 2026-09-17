using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Divisions;

/// <summary>
/// Все подразделения плоским списком (дерево строит страница по <c>ParentId</c>). Справочник питает решётку
/// доступа (допуски по подразделениям, ТБ-020) и формы дел (ТБ-024).
/// </summary>
/// <remarks>
/// Чтение открыто любому вошедшему: наименования подразделений нужны карточкам дел и фильтрам, а режимных
/// сведений в них нет. Запись (создание/переименование/выключение) — только Администратору.
/// </remarks>
public sealed record ListDivisionsQuery : IRequest<ResponseDto<IReadOnlyList<DivisionNode>>>
{
    /// <inheritdoc cref="ListDivisionsQuery" />
    public sealed class Handler(IDivisionAdminStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<ListDivisionsQuery, ResponseDto<IReadOnlyList<DivisionNode>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<DivisionNode>>> Handle(
            ListDivisionsQuery query, CancellationToken cancellationToken)
        {
            // Fail-closed: неаутентифицированному — ничего, даже справочник.
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is null)
            {
                return ResponseDto<IReadOnlyList<DivisionNode>>.BadRequest(
                    "Справочник подразделений доступен только вошедшим пользователям.");
            }

            var items = await store.ListAsync(cancellationToken);
            return ResponseDto<IReadOnlyList<DivisionNode>>.Ok(items, items.Count);
        }
    }
}
