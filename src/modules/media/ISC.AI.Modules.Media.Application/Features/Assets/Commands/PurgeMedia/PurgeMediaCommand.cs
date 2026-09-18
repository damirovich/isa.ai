using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Гарантированное удаление носителя и всех биометрических производных (ТБ-064/075, GATE-6).
/// НЕ <see cref="Abstractions.Audit.IAuditableRequest"/>: аудит <c>Purge</c> пишет сам <see cref="IMediaPurger"/>
/// — ПЕРВЫМ и fail-closed (нет записи — нет уничтожения); вторая запись поведением была бы дублем.
/// </summary>
public sealed record PurgeMediaCommand(int AssetId) : IRequest<ResponseDto<MediaPurgeResult>>
{
    /// <inheritdoc cref="PurgeMediaCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaPurger purger)
        : IRequestHandler<PurgeMediaCommand, ResponseDto<MediaPurgeResult>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaPurgeResult>> Handle(
            PurgeMediaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanPurgeAsync(cancellationToken))
            {
                return ResponseDto<MediaPurgeResult>.BadRequest("Гарантированное удаление носителей доступно ролям Администратор/Руководитель.");
            }

            // Fail-closed (ТБ-020/021): удалить можно только то, что субъекту доступно; недоступный носитель
            // неотличим от несуществующего — иначе сам ответ «нельзя» выдал бы факт существования.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<MediaPurgeResult>.NotFound("Носитель не найден или недоступен.");
            }

            // Сужение по делам субъекта поверх решётки (ТБ-071, ТФ-ДЕЛ-03): уничтожить чужой носитель по
            // перебираемому идентификатору нельзя; отказ неотличим от «не найден».
            if (!await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<MediaPurgeResult>.NotFound("Носитель не найден или недоступен.");
            }

            var result = await purger.PurgeAsync(asset.Id, access.NumericSubjectId, cancellationToken);
            return ResponseDto<MediaPurgeResult>.Ok(result);
        }
    }
}
