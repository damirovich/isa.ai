using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Scope;

/// <summary>
/// Одно лицо носителя по идентификатору (ТФ-ПЛ-03): нужно интерфейсу, когда проба поиска или пара
/// верификации ссылается на лицо уже проиндексированного носителя — только по <c>FaceId</c> нельзя
/// построить ссылку на вырезку (маршрут раздачи требует носитель). Просмотр биометрического
/// материала аудируется (ТБ-030); недоступное лицо неотличимо от несуществующего (ТБ-020/021).
/// </summary>
public sealed record GetFaceQuery(int FaceId) : IRequest<ResponseDto<FaceRow>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.View;

    /// <inheritdoc />
    public string? AuditSummary => $"media:face:{FaceId}:view";

    /// <inheritdoc cref="GetFaceQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, IMediaCatalog catalog)
        : IRequestHandler<GetFaceQuery, ResponseDto<FaceRow>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<FaceRow>> Handle(GetFaceQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-020/021): решётка применяется каталогом на стороне БД.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var face = await catalog.GetFaceAsync(query.FaceId, access, cancellationToken);
            return face is null
                ? ResponseDto<FaceRow>.NotFound("Лицо не найдено или недоступно.")
                : ResponseDto<FaceRow>.Ok(face);
        }
    }
}
