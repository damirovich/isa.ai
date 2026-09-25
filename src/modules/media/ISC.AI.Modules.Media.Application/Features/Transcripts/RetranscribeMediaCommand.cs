using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.BackgroundTasks;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;
using Microsoft.Extensions.DependencyInjection;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>
/// Расшифровать (повторно) речь носителя (ADR-0026, предлагаемый ТФ-МЕД-08): после сбоя, после смены модели
/// или для видео, загруженного до появления расшифровки. Ставит задачу в фоновую очередь; возвращает
/// идентификатор фоновой задачи. Право — как у загрузки и переиндексации (ТП-004).
/// </summary>
/// <remarks>
/// Режим — как у <c>ReindexMediaCommand</c>: решётка на стороне БД (каталог), ПОВЕРХ неё — сужение по делам
/// субъекта (ТБ-071); запустить обработку чужого носителя по перебираемому идентификатору нельзя, отказ
/// неотличим от «не найден» (ТБ-020/021). Запуск аудируется (ТБ-030, <see cref="AuditAction.Modify"/>);
/// итог расшифровки конвейер пишет в журнал сам. Прежняя расшифровка остаётся видна, пока новая не записана
/// целиком (её заменяет только успешный прогон).
/// </remarks>
/// <param name="AssetId">Носитель (аудио или видео).</param>
public sealed record RetranscribeMediaCommand(int AssetId) : IRequest<ResponseDto<Guid>>, IAuditableRequest
{
    /// <summary>Вид фоновой задачи (показывается в списке задач).</summary>
    public const string TaskKind = "Повторная расшифровка речи носителя";

    /// <summary>Отказ, когда расшифровка носителя уже в очереди или выполняется (второй прогон не ставится).</summary>
    public const string AlreadyInProgressMessage = "Расшифровка уже выполняется — нажмите «Обновить».";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary => "media:retranscribe:asset=" + AssetId.ToString(CultureInfo.InvariantCulture);

    /// <inheritdoc cref="RetranscribeMediaCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IMediaStore store,
        IBackgroundTaskQueue queue)
        : IRequestHandler<RetranscribeMediaCommand, ResponseDto<Guid>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<Guid>> Handle(RetranscribeMediaCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<Guid>.BadRequest("Расшифровка носителей доступна ролям Следователь и Администратор.");
            }

            // Fail-closed (ТБ-020/021): носитель вне допуска неотличим от несуществующего.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null)
            {
                return ResponseDto<Guid>.NotFound("Носитель не найден или недоступен.");
            }

            // Сужение по делам субъекта поверх решётки (ТБ-071, ТФ-ДЕЛ-03).
            if (!await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<Guid>.NotFound("Носитель не найден или недоступен.");
            }

            if (asset.Kind == MediaKind.Image)
            {
                return ResponseDto<Guid>.BadRequest("К изображению расшифровка неприменима.");
            }

            // Статус «в очереди» — сразу, до постановки: карточка показывает, что работа принята. Перевод УСЛОВНЫЙ,
            // одним UPDATE в БД: пока расшифровка в очереди или идёт, второй полный прогон той же записи в общую
            // последовательную очередь не ставится (он задержал бы и чужие задачи); два параллельных нажатия не
            // пройдут оба. Носитель уже найден выше — ноль строк означает «уже выполняется».
            var assetId = asset.Id; // захватываем только примитив — scope запроса к моменту выполнения уже закрыт
            if (!await store.TryMarkTranscriptionPendingAsync(assetId, cancellationToken))
            {
                return ResponseDto<Guid>.BadRequest(AlreadyInProgressMessage);
            }

            var taskId = await queue.EnqueueAsync(
                TaskKind,
                async (sp, ct) => await sp.GetRequiredService<IMediaTranscriptionPipeline>().TranscribeAsync(assetId, ct),
                cancellationToken);

            return ResponseDto<Guid>.Ok(taskId);
        }
    }
}
