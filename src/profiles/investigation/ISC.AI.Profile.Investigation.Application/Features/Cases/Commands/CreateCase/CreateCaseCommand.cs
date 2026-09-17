using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Application.Features.Common;
using ISC.AI.Profile.Investigation.Domain.Enums;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Завести дело (ТФ-ДЕЛ-01). Гриф и подразделение обязательны и БЕЗ умолчаний (ТБ-024): их наследуют
/// носители, фигуранты и шаблоны лиц (ТБ-070). Заводят Следователь, Руководитель, Администратор.
/// </summary>
public sealed record CreateCaseCommand(
    string Number,
    string Title,
    CaseKind Kind,
    DateOnly OpenedAt,
    int? InvestigatorUserId,
    int DivisionId,
    short Classification,
    string? Basis = null)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    /// <remarks>Номер и реквизиты дела — режимные сведения: в сводку идут только вид, подразделение и гриф (ТБ-032).</remarks>
    public string? AuditSummary =>
        $"investigation:case:create:kind={Kind};division={DivisionId};grif={Classification}";

    /// <inheritdoc />
    /// <remarks>Запись журнала — не ниже грифа заводимого дела (ТБ-032).</remarks>
    public short? AuditClassification => Classification;

    /// <inheritdoc cref="CreateCaseCommand" />
    public sealed class Handler(
        ICaseStore cases, IUserRoleStore roles, ISubjectProvider subjectProvider, IAccessContextProvider accessProvider)
        : IRequestHandler<CreateCaseCommand, ResponseDto<int>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(CreateCaseCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Роль проверяется В ОБРАБОТЧИКЕ, а не только на странице (ТП-004): иначе любой новый
            // вызывающий (другая страница, будущий API) обошёл бы ограничение.
            if (!await RoleGuard.CallerCanEditCasesAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(RoleGuard.CaseDenied);
            }

            // Fail-closed (ТБ-021): без допуска дело завести нельзя — хранилище сверяет гриф/подразделение
            // дела с допуском субъекта (ТБ-024), выше своего допуска дело не заводится.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var draft = new CaseDraft(
                command.Number.Trim(), command.Title.Trim(), command.Kind, command.OpenedAt,
                command.InvestigatorUserId, command.DivisionId, command.Classification,
                string.IsNullOrWhiteSpace(command.Basis) ? null : command.Basis.Trim(),
                access.NumericSubjectId);

            var (result, caseId) = await cases.CreateAsync(draft, access, cancellationToken);
            return result switch
            {
                CaseWriteResult.Ok => ResponseDto<int>.Ok(caseId),
                CaseWriteResult.DuplicateNumber => ResponseDto<int>.Conflict(CaseGuard.DuplicateNumber),
                CaseWriteResult.OutsideClearance => ResponseDto<int>.BadRequest(CaseGuard.OutsideClearance),
                _ => ResponseDto<int>.NotFound(CaseGuard.NotFound),
            };
        }
    }
}
