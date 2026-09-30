using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Audit;

/// <summary>
/// Сотрудники для отбора «Кто» в журнале аудита — по имени, а не по номеру (ТБ-030). Включая отключённых: их действия
/// остаются в журнале. Право — то же, что на сам журнал (<see cref="IPlatformAdministration.CanViewAuditAsync"/>):
/// читатель журнала (например, Офицер ИБ) экрана «Пользователи» может не видеть, а выбрать сотрудника должен.
/// </summary>
public sealed record ListAuditSubjectsQuery : IRequest<ResponseDto<IReadOnlyList<AuditSubject>>>
{
    /// <inheritdoc cref="ListAuditSubjectsQuery" />
    public sealed class Handler(IUserAccountStore accounts, IPlatformAdministration administration)
        : IRequestHandler<ListAuditSubjectsQuery, ResponseDto<IReadOnlyList<AuditSubject>>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<IReadOnlyList<AuditSubject>>> Handle(
            ListAuditSubjectsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            if (!await administration.CanViewAuditAsync(cancellationToken))
            {
                return ResponseDto<IReadOnlyList<AuditSubject>>.BadRequest(AdminGuard.AuditDenied);
            }

            // Только имена и имена входа — без допусков и должностей: читателю журнала больше не нужно.
            IReadOnlyList<AuditSubject> subjects =
            [
                .. (await accounts.ListAsync(cancellationToken))
                    .Select(a => new AuditSubject(
                        a.UserId, string.IsNullOrWhiteSpace(a.DisplayName) ? a.UserName : a.DisplayName, a.UserName, a.IsActive))
                    .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase),
            ];
            return ResponseDto<IReadOnlyList<AuditSubject>>.Ok(subjects, subjects.Count);
        }
    }
}
