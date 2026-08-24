using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт реестра методик (§5.2.9, Ц-03). Порт — в домене, реализация — в слое данных (схема
/// <c>inspector</c>). РЕЖИМ: каждый метод принимает допуск субъекта (<c>maxClassification</c>) и
/// фильтрует В ЗАПРОСЕ — методика с грифом выше допуска неотличима от несуществующей (ТБ-020-стиль).
/// </summary>
public interface IMethodRegistryStore
{
    /// <summary>Сохраняет методику (статус — черновик); возвращает id.</summary>
    Task<int> SaveAsync(MethodDocumentDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Страница реестра с отбором; свежие первыми.</summary>
    Task<MethodPage> ListAsync(
        MethodListFilter filter, short maxClassification, CancellationToken cancellationToken = default);

    /// <summary>Карточка; <see langword="null"/> — не найдена или недоступна по допуску.</summary>
    Task<MethodDocumentDetails?> GetAsync(
        int methodId, short maxClassification, CancellationToken cancellationToken = default);

    /// <summary>
    /// Правит текст. Утверждённая при правке ВОЗВРАЩАЕТСЯ в черновики (утверждение относится
    /// к конкретной редакции), отметка утверждения снимается.
    /// </summary>
    Task<MethodWriteResult> UpdateBodyAsync(
        int methodId, string body, short maxClassification, CancellationToken cancellationToken = default);

    /// <summary>Меняет статус (утвердить / вернуть в черновики); при утверждении пишет кто.</summary>
    Task<MethodWriteResult> SetStatusAsync(
        int methodId, MethodDocumentStatus status, int? approvedByUserId, short maxClassification,
        CancellationToken cancellationToken = default);

    /// <summary>Удаляет ЧЕРНОВИК; утверждённая не удаляется (<see cref="MethodWriteResult.NotDraft"/>).</summary>
    Task<MethodWriteResult> DeleteAsync(
        int methodId, short maxClassification, CancellationToken cancellationToken = default);
}
