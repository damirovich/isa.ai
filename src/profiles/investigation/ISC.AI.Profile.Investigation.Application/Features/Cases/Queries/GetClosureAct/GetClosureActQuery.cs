using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Investigation.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>
/// Акт об удалении биометрических шаблонов по закрытию дела (ТФ-ДЕЛ-04, ТБ-074) для карточки дела.
/// </summary>
/// <remarks>
/// НЕ <see cref="Abstractions.Audit.IAuditableRequest"/> намеренно: акт читается вместе с карточкой
/// дела, а обращение к карточке уже записано <see cref="GetCaseQuery"/>. Вторая запись на то же
/// открытие страницы — дубль в журнале (та же ошибка, что ловили на предрендере страниц медиа):
/// журнал должен отвечать «сколько раз смотрели дело», а не «сколько запросов сделала страница».
///
/// Отсутствие акта у ЗАКРЫТОГО дела — не пустота, а сигнал: регламент не исполнен (скорее всего был
/// недоступен журнал аудита, и удаление правильно не состоялось). Карточка показывает это
/// предупреждением, поэтому запрос возвращает <see langword="null"/> без ошибки.
/// </remarks>
public sealed record GetClosureActQuery(int CaseId) : IRequest<ResponseDto<CaseClosureActRow?>>
{
    /// <inheritdoc cref="GetClosureActQuery" />
    public sealed class Handler(ICaseStore cases, IAccessContextProvider accessProvider)
        : IRequestHandler<GetClosureActQuery, ResponseDto<CaseClosureActRow?>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<CaseClosureActRow?>> Handle(
            GetClosureActQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Решётка — внутри хранилища: акт виден ровно тем, кому видно дело (ТБ-020/021, ТФ-ДЕЛ-03).
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var act = await cases.GetClosureActAsync(query.CaseId, access, cancellationToken);

            return ResponseDto<CaseClosureActRow?>.Ok(act);
        }
    }
}
