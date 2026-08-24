using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Метаданные документа для чужой слабой ссылки (по значению RegNumber, ТО-инф-06).</summary>
public sealed record DocumentRefCard(
    int Id,
    string RegNumber,
    DateOnly RegDate,
    string TypeName,
    int? InspectorUserId,
    DocumentAggregatedStatus AggregatedStatus,
    int DivisionId);

/// <summary>
/// Разрешение слабых ссылок «по значению» на документы модуля: другой модуль (например, учёт
/// нарушений профиля — поле «справка-проверка») хранит RegNumber строкой без FK через границу схем
/// (ТО-инф-06) и через этот порт обогащает свои карточки метаданными документа.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ДОСТУПА (ТБ-020/021): выдача проходит ту же решётку видимости, что список документов
/// (<c>AccessFilterExtensions.VisibleTo</c>). Недоступный документ в ответе ОТСУТСТВУЕТ — вызывающий
/// показывает голый номер, как его и ввели; сам номер секретом не является (он уже хранится у
/// вызывающего). Fail-closed: пустой допуск — пустой словарь.
/// </remarks>
public interface IDocumentLookup
{
    /// <summary>
    /// Карточки документов по регистрационным номерам: ключ — RegNumber, недоступные и
    /// несуществующие номера в словаре отсутствуют. Номера сверх предела партии игнорируются
    /// (защита от неограниченного IN — вызывающий работает страницами).
    /// </summary>
    Task<IReadOnlyDictionary<string, DocumentRefCard>> ResolveByRegNumbersAsync(
        IReadOnlyCollection<string> regNumbers, AccessContext access, CancellationToken cancellationToken = default);
}
