using ISC.AI.Modules.DocFlow.Application.Features.Directory;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.UI;

/// <summary>
/// Кэш кандидатов-исполнителей по подразделению (§4.1) для форм с выбором исполнителя.
/// Вынесен из <c>DocumentRegister</c> и <c>AssignmentsSection</c>, где жил двумя дословными
/// копиями, — тонкая защита от повторных запросов при перерисовке должна существовать один раз.
/// </summary>
/// <remarks>
/// Список для КАЖДОГО подразделения запрашивается один раз и запоминается: форма перерисовывается
/// на каждое нажатие, и без кэша сервер получал бы запрос при вводе любого другого поля.
/// Пустой ответ кладётся в кэш СРАЗУ при старте загрузки — он же служит отметкой «запрос уже
/// в пути», иначе следующая перерисовка отправила бы второй такой же. Когда список приходит,
/// компонент узнаёт об этом через <paramref name="onLoaded"/> (обычно <c>StateHasChanged</c>).
/// </remarks>
public sealed class AssigneeCandidatesCache(IMediator dispatcher, Action onLoaded)
{
    private readonly Dictionary<int, IReadOnlyList<UserItem>> _byDivision = [];

    /// <summary>Кандидаты для подразделения; пусто — подразделение не выбрано или список ещё в пути.</summary>
    public IReadOnlyList<UserItem> Get(int? divisionId)
    {
        if (divisionId is not { } id)
        {
            return [];
        }

        if (_byDivision.TryGetValue(id, out var cached))
        {
            return cached;
        }

        _byDivision[id] = [];
        _ = LoadAsync(id);
        return [];
    }

    private async Task LoadAsync(int divisionId)
    {
        var response = await dispatcher.Send(new ListAssigneeCandidatesQuery(divisionId));
        if (response is { Status: true, Data: { } list })
        {
            _byDivision[divisionId] = list;
            onLoaded();
        }
    }
}
