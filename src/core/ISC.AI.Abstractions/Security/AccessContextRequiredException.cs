namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Бросается, когда извлечение/доступ к корпусу вызваны БЕЗ контекста доступа субъекта.
/// Реализует fail-closed (ТБ-012, ТБ-021): без установленного допуска операция не выполняется,
/// а не выполняется «без фильтра».
/// </summary>
public sealed class AccessContextRequiredException : Exception
{
    /// <summary>Создаёт исключение с сообщением по умолчанию.</summary>
    public AccessContextRequiredException()
        : base("Извлечение невозможно без контекста доступа субъекта (fail-closed, ТБ-012/021).")
    {
    }
}
