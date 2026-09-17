using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>
/// Повторно проиндексировать носитель (ТФ-МЕД-02, ТО-мат-09: после смены моделей или сбоя). Ставит задачу
/// в фоновую очередь; возвращает идентификатор фоновой задачи. Право — как у загрузки (ТП-004).
/// </summary>
public sealed record ReindexMediaCommand(int AssetId) : IRequest<ResponseDto<Guid>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => $"media:reindex:asset={AssetId}";

    /// <inheritdoc cref="ReindexMediaCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        IBackgroundTaskQueue queue)
        : IRequestHandler<ReindexMediaCommand, ResponseDto<Guid>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<Guid>> Handle(ReindexMediaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<Guid>.BadRequest("Переиндексация носителей доступна ролям Следователь/Администратор.");
            }

            // Fail-closed (ТБ-020/021): носитель вне допуска неотличим от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<Guid>.NotFound("Носитель не найден или недоступен.");
            }

            var assetId = asset.Id; // захватываем только примитив — scope запроса к моменту выполнения уже закрыт
            var taskId = await queue.EnqueueAsync(
                "Переиндексация носителя (распознавание лиц)",
                async (sp, ct) => await sp.GetRequiredService<IMediaIndexer>().IndexAsync(assetId, ct),
                cancellationToken);

            return ResponseDto<Guid>.Ok(taskId);
        }
    }
}
