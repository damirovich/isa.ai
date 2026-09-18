using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Scope;

/// <summary>Границы текущего субъекта для форм: допуск, его подразделения, роль и идентификатор.</summary>
/// <param name="MaxClassification">Максимальный доступный гриф.</param>
/// <param name="OwnerDivisions">Подразделения справочника, входящие в допуск субъекта.</param>
/// <param name="Role">Роль профиля; <see langword="null"/> — не назначена.</param>
/// <param name="UserId">Идентификатор пользователя ядра; <see langword="null"/> — не отображается в реестр.</param>
public sealed record AccessScope(
    short MaxClassification,
    IReadOnlyList<DivisionNode> OwnerDivisions,
    InvestigationRole? Role,
    int? UserId);

/// <summary>
/// Пределы субъекта — чтобы форма дела не предлагала заведомо запрещённые гриф/подразделение (ТБ-024).
/// </summary>
/// <remarks>
/// Это УДОБСТВО, а не защита: серверная проверка в <c>ICaseStore.CreateAsync</c> остаётся и срабатывает
/// при запросе в обход формы, а также в гонке «допуск отозвали, пока форма была открыта».
/// </remarks>
public sealed record GetAccessScopeQuery : IRequest<ResponseDto<AccessScope>>
{
    /// <summary>Текст отказа, когда допуск не установлен (fail-closed, ТБ-021).</summary>
    public const string NoClearance = "Не удалось определить ваш допуск: обратитесь к администратору.";

    /// <inheritdoc cref="GetAccessScopeQuery" />
    public sealed class Handler(
        IAccessContextProvider accessProvider,
        IDivisionAdminStore divisions,
        IUserRoleStore roles,
        ISubjectProvider subjectProvider)
        : IRequestHandler<GetAccessScopeQuery, ResponseDto<AccessScope>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AccessScope>> Handle(GetAccessScopeQuery query, CancellationToken cancellationToken)
        {
            AccessContext access;
            try
            {
                // Fail-closed (ТБ-012/021): без допуска — понятный отказ вместо падения страницы.
                access = await accessProvider.GetCurrentAsync(cancellationToken);
            }
            catch (AccessContextRequiredException)
            {
                return ResponseDto<AccessScope>.BadRequest(NoClearance);
            }

            var userId = await subjectProvider.GetCurrentUserIdAsync(cancellationToken);
            var role = userId is { } id ? await roles.GetRoleAsync(id, cancellationToken) : null;
            var allowed = access.AllowedDivisions;
            var own = (await divisions.ListAsync(cancellationToken)).Where(d => allowed.Contains(d.Id)).ToList();

            return ResponseDto<AccessScope>.Ok(new AccessScope(access.MaxClassification, own, role, userId));
        }
    }
}
