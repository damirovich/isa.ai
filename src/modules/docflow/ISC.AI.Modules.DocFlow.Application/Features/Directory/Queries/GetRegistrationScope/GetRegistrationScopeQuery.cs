using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Directory;

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
