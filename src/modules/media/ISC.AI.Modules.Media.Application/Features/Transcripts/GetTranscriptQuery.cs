using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>
/// Расшифровка речи носителя для карточки (ADR-0026, предлагаемый ТФ-МЕД-08): состояние, модель и фрагменты с
/// таймкодами для перехода к месту записи. Недоступный носитель неотличим от несуществующего (ТБ-020/021).
/// </summary>
/// <remarks>
/// НЕ <see cref="IAuditableRequest"/> — намеренно. Расшифровка показывается ТОЛЬКО в карточке носителя и
/// загружается вместе с ней, а открытие карточки уже аудируется (<c>GetMediaAssetQuery</c>, ТБ-030:
/// <c>media:asset:{id}:view</c>). Одна страница — одна запись журнала: вторая запись на то же открытие дублировала
/// бы первую и засоряла журнал, не добавляя ответа на вопрос «кто что смотрел». Сама расшифровка — материал того
/// же носителя под тем же грифом. Поиск по расшифровкам — другое действие (новый вопрос к материалам дела) и
/// аудируется отдельно (<see cref="SearchTranscriptsQuery"/>).
///
/// Режим — тот же, что у карточки: решётка на стороне БД по носителю и по каждому фрагменту (каталог), ПОВЕРХ неё —
/// сужение по делам субъекта (ТБ-071): следователь того же подразделения и допуска перебором идентификаторов не
/// должен читать расшифровки чужих дел. Без контекста доступа — отказ (fail-closed, ТБ-021).
/// </remarks>
/// <param name="AssetId">Носитель.</param>
public sealed record GetTranscriptQuery(int AssetId) : IRequest<ResponseDto<MediaTranscript>>
{
    /// <summary>Единый ответ на «нет» и «нельзя» (ТБ-020/021): причина отказа наружу не различается.</summary>
    public const string NotFoundMessage = "Носитель не найден или недоступен.";

    /// <inheritdoc cref="GetTranscriptQuery" />
    public sealed class Handler(IAccessContextProvider accessProvider, IMediaCatalog catalog, ICaseScope caseScope)
        : IRequestHandler<GetTranscriptQuery, ResponseDto<MediaTranscript>>
    {
        /// <inheritdoc />
        public async ValueTask<ResponseDto<MediaTranscript>> Handle(GetTranscriptQuery query, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(query);

            // Fail-closed (ТБ-021): без контекста допуска GetCurrentAsync бросает — чтения нет.
            var access = await accessProvider.GetCurrentAsync(cancellationToken);

            // Сужение по делам субъекта (ТБ-071, ТФ-ДЕЛ-03) — ДО чтения фрагментов: чужой носитель не читается
            // вовсе. Отказ неотличим от «не найден» (ТБ-020/021).
            if (!await caseScope.IsAssetAccessibleAsync(query.AssetId, access, cancellationToken))
            {
                return ResponseDto<MediaTranscript>.NotFound(NotFoundMessage);
            }

            // Решётка гриф/подразделение — на стороне БД (каталог): по носителю и по каждому фрагменту.
            var transcript = await catalog.GetTranscriptAsync(query.AssetId, access, cancellationToken);
            return transcript is null
                ? ResponseDto<MediaTranscript>.NotFound(NotFoundMessage)
                : ResponseDto<MediaTranscript>.Ok(transcript);
        }
    }
}
