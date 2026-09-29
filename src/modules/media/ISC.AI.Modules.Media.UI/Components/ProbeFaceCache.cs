using ISC.AI.Abstractions.Application;
using ISC.AI.Modules.Media.Application.Features.Scope;
using ISC.AI.Modules.Media.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Лица проб на ОДНУ загрузку страницы очереди верификации: карточки одной поисковой сессии делят одну пробу,
/// и без кэша каждая пара «проба ↔ кандидат» слала свой <see cref="GetFaceQuery"/> — запись аудита и несколько
/// запросов к БД на карточку. С кэшем — один запрос на лицо пробы.
/// </summary>
/// <remarks>
/// Кэш живёт ровно одну загрузку страницы (страница создаёт новый при каждом «Обновить»/переходе): допуск
/// субъекта мог измениться, поэтому ответ не переиспользуется между загрузками; каждое открытие страницы
/// попадает в журнал своим <see cref="GetFaceQuery"/> (ТБ-030). Отказ (лицо вне допуска или удалено)
/// кэшируется так же, как успех, — причины не различаются (ТБ-020/021). Потокобезопасность не нужна:
/// компоненты Blazor Server выполняются в контексте синхронизации схемы (circuit) по одному.
/// </remarks>
public sealed class ProbeFaceCache
{
    private readonly Dictionary<int, Task<ResponseDto<FaceRow>>> _faces = [];

    /// <summary>Лицо пробы: первый вызов шлёт запрос, остальные ждут тот же ответ.</summary>
    /// <param name="faceId">Лицо пробы.</param>
    /// <param name="dispatcher">Mediator компонента.</param>
    public Task<ResponseDto<FaceRow>> GetAsync(int faceId, IMediator dispatcher)
    {
        ArgumentNullException.ThrowIfNull(dispatcher);

        if (!_faces.TryGetValue(faceId, out var face))
        {
            face = dispatcher.Send(new GetFaceQuery(faceId)).AsTask();
            _faces[faceId] = face;
        }

        return face;
    }
}
