using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Suggestions;

/// <summary>
/// «Сверить сейчас» на карточке носителя (ТФ-ПЕР-09): поставить сверку лиц носителя с эталонами фигурантов его дел в
/// фоновую очередь. Нужна для материалов, загруженных до появления эталона или основания, и как запасной путь, если
/// автоматическая сверка не поставилась. Права — как у переиндексации (Следователь, Администратор).
/// </summary>
/// <remarks>
/// Сверку выполняет система под потолком каждого дела, как при индексации (ADR-0035): кнопка не расширяет область и
/// не меняет правил. Запрос сотрудника пишется в журнал этой командой (ТБ-072), итог сверки — записями системы.
/// </remarks>
/// <param name="AssetId">Носитель.</param>
public sealed record RequestPersonSuggestionCommand(int AssetId) : IRequest<ResponseDto<Guid>>, IAuditableRequest
{
    /// <summary>Единый ответ «нет или нельзя» (ТБ-020/021).</summary>
    public const string NotFoundMessage = "Носитель не найден или недоступен.";

    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Search;

    /// <inheritdoc />
    public string? AuditSummary => FormattableString.Invariant($"media:suggest:request:asset={AssetId}");

    /// <inheritdoc cref="RequestPersonSuggestionCommand" />
    public sealed class Handler(
        IMediaAdministration administration,
        IAccessContextProvider accessProvider,
        IMediaCatalog catalog,
        ICaseScope caseScope,
        IPersonSuggestionScheduler scheduler,
        MediaSearchOptions options)
        : IRequestHandler<RequestPersonSuggestionCommand, ResponseDto<Guid>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<Guid>> Handle(RequestPersonSuggestionCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!options.AutoSuggestEnabled)
            {
                return ResponseDto<Guid>.BadRequest(
                    "Автоматическая сверка с эталонами выключена настройкой " + MediaSearchOptions.AutoSuggestEnabledKey + ".");
            }

            if (!await administration.CanUploadAsync(cancellationToken))
            {
                return ResponseDto<Guid>.BadRequest("Сверку с эталонами запускают роли Следователь и Администратор.");
            }

            // Fail-closed (ТБ-020/021, ТБ-071): решётка носителя и сужение по делам субъекта; отказ неотличим от «нет».
            var access = await accessProvider.GetCurrentAsync(cancellationToken);
            var asset = await catalog.GetAsync(command.AssetId, access, cancellationToken);
            if (asset is null || !await caseScope.IsAssetAccessibleAsync(asset.Id, access, cancellationToken))
            {
                return ResponseDto<Guid>.NotFound(NotFoundMessage);
            }

            if (asset.Kind == MediaKind.Audio)
            {
                return ResponseDto<Guid>.BadRequest("В аудиозаписи лиц нет — сверять нечего.");
            }

            if (asset.IndexStatus != MediaIndexStatus.Indexed)
            {
                return ResponseDto<Guid>.BadRequest("Носитель ещё не обработан — сверка пройдёт сама после обработки.");
            }

            if (asset.FaceCount == 0)
            {
                return ResponseDto<Guid>.BadRequest("На носителе не найдено лиц — сверять нечего.");
            }

            var taskId = await scheduler.ScheduleAssetAsync(asset.Id, SuggestionTrigger.Manual, cancellationToken);
            return taskId is { } id
                ? ResponseDto<Guid>.Ok(id)
                : ResponseDto<Guid>.BadRequest("Не удалось поставить сверку в очередь — повторите позже.");
        }
    }
}
