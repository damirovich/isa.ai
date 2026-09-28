using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.References;

/// <summary>
/// Записи справочников профиля (ТФ-АДМ-07), включая выключенные: формы предлагают только действующие, но
/// выключенная запись нужна, чтобы старое задание показывало своего инициатора по имени, а не номером.
/// </summary>
/// <param name="Kind">Вид справочника; <see langword="null"/> — все справочники.</param>
/// <remarks>
/// Чтение открыто любому вошедшему (как у справочника подразделений): наименования ГУ, званий и типов связей
/// нужны формам и карточкам, режимных сведений в них нет. Запись — только Администратору.
/// </remarks>
public sealed record ListReferenceItemsQuery(ReferenceKind? Kind = null) : IRequest<ResponseDto<IReadOnlyList<ReferenceItemRow>>>
{
    /// <inheritdoc cref="ListReferenceItemsQuery" />
    public sealed class Handler(IReferenceStore store, ISubjectProvider subjectProvider)
        : IRequestHandler<ListReferenceItemsQuery, ResponseDto<IReadOnlyList<ReferenceItemRow>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<ReferenceItemRow>>> Handle(
            ListReferenceItemsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed: неаутентифицированному — ничего, даже справочник.
            if (await subjectProvider.GetCurrentUserIdAsync(cancellationToken) is null)
            {
                return ResponseDto<IReadOnlyList<ReferenceItemRow>>.BadRequest(
                    "Справочники доступны только вошедшим пользователям.");
            }

            var items = await store.ListAsync(query.Kind, cancellationToken);
            return ResponseDto<IReadOnlyList<ReferenceItemRow>>.Ok(items, items.Count);
        }
    }
}
