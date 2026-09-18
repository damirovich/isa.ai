using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Admin.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Admin.Application.Features.Audit;

/// <summary>
/// Просмотр неизменяемого журнала аудита (ТБ-030/032).
/// </summary>
/// <param name="Filter">Отбор записей: период, субъект, действие, объект, страница.</param>
/// <remarks>
/// Право просмотра определяется РОЛЬЮ, а роли ведёт профиль — поэтому пакет спрашивает отдельный
/// вопрос <c>CanViewAuditAsync</c>: в профиле «Следствие» журнал читает не только Администратор, но
/// и Офицер ИБ, который учётками и допусками не распоряжается. Ядро отдаёт нейтральный порт
/// <see cref="IAuditReader"/>, который решает только режимную часть — что субъекту видно по грифу
/// и подразделению.
/// </remarks>
public sealed record ListAuditRecordsQuery(AuditFilter Filter)
    : IRequest<ResponseDto<AuditPage>>, IAuditableRequest
{
    /// <inheritdoc />
    /// <remarks>
    /// Чтение журнала САМО пишется в журнал: «кто смотрел историю» — такое же обращение к режимным
    /// данным, как просмотр документа, и оставлять его без следа нельзя. Рекурсии тут нет: запись
    /// о просмотре ничего не читает.
    /// </remarks>
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => "core:audit:view";

    /// <inheritdoc cref="ListAuditRecordsQuery" />
    public sealed class Handler(
        IAuditReader reader,
        IAccessContextProvider accessProvider,
        IPlatformAdministration administration)
        : IRequestHandler<ListAuditRecordsQuery, ResponseDto<AuditPage>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<AuditPage>> Handle(
            ListAuditRecordsQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // ИНВАРИАНТ (ТБ-030/032): право ОТКРЫТЬ журнал — отдельное от права администрировать,
            // и знает его профиль. Пакет о ролях не догадывается.
            if (!await administration.CanViewAuditAsync(cancellationToken))
            {
                return ResponseDto<AuditPage>.BadRequest(AdminGuard.AuditDenied);
            }

            try
            {
                // Режимная часть — в порту: даже администратор не видит записей выше своего допуска
                // (ТБ-032). Право даёт ОТКРЫТЬ журнал, а не видеть в нём всё.
                var access = await accessProvider.GetCurrentAsync(cancellationToken);
                var page = await reader.QueryAsync(query.Filter, access, cancellationToken);

                return ResponseDto<AuditPage>.Ok(page, page.TotalCount);
            }
            catch (AccessContextRequiredException)
            {
                // FAIL-CLOSED (ТБ-012/021): контекст доступа обязателен, и провайдер правильно
                // отказывает администратору БЕЗ записи в core.clearance. Но на чистом контуре это
                // штатное состояние (допуска нет ни у кого), и необработанное исключение роняло бы
                // страницу с 500 вместо объяснения. Ослабления режима здесь нет: записей не выдано
                // ни одной, изменился только текст отказа.
                return ResponseDto<AuditPage>.BadRequest(AdminGuard.AuditClearanceRequired);
            }
        }
    }
}
