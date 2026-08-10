using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Norms;

using ResModel = ResponseDto<int>;

/// <summary>
/// Сменить статус редакции НПА (Э4-02, ТФ-НПА-02): доменный статус + материализация видимости связанных
/// чанков в ядре (утратившая силу перестаёт выдаваться как действующая, GATE-3).
/// </summary>
/// <remarks>
/// До картотеки (2026-08-10) команда жила без гарда и без аудита — «дыра-прецедент»: смена статуса
/// меняет выдачу поиска и грунтовки для ВСЕХ пользователей и обязана быть и ограничена, и в журнале.
/// Закрыто вместе с постройкой реестра; заодно команда переехала из среза Revisions в Norms —
/// у картотеки один агрегат.
/// </remarks>
/// <param name="NormRevisionId">Идентификатор редакции.</param>
/// <param name="Status">Новый статус (действующая/утратила силу).</param>
public sealed record SetRevisionStatusCommand(int NormRevisionId, RevisionStatus Status)
    : IRequest<ResModel>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"inspector:norm-revision:{NormRevisionId}:status:{Status}";

    /// <summary>Обработчик: гард ведения картотеки, затем доменная служба материализации.</summary>
    public sealed class Handler(
        IRevisionStatusMaterializer materializer, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SetRevisionStatusCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(SetRevisionStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await NormGuard.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResModel.BadRequest(NormGuard.Denied);
            }

            var affectedChunks = await materializer.SetStatusAsync(command.NormRevisionId, command.Status, cancellationToken);
            return ResModel.Ok(affectedChunks, $"Статус редакции обновлён; затронуто чанков: {affectedChunks}.");
        }
    }
}
