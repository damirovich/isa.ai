namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Разрешение слабых ссылок «справка-проверка» (RegNumber строкой, ТО-инф-06) в карточки документов
/// документооборота — ОТ ИМЕНИ ТЕКУЩЕГО субъекта: недоступный по решётке документ (ТБ-020/021)
/// в ответе отсутствует, и архив показывает голый номер, как его ввели в нарушении.
/// </summary>
public interface IInspectionDocumentResolver
{
    /// <summary>Карточки по номерам; ключ — RegNumber, недоступные/несуществующие отсутствуют.</summary>
    Task<IReadOnlyDictionary<string, InspectionDocumentCard>> ResolveAsync(
        IReadOnlyCollection<string> regNumbers, CancellationToken cancellationToken = default);
}
