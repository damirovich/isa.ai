namespace ISC.AI.Abstractions.Security;

/// <summary>
/// Источник контекста доступа текущего субъекта (ТБ-012). ШОВ под будущую интеграцию: до этапа
/// аутентификации (Э3-08) — dev-заглушка; затем — реализация поверх ВНЕШНЕЙ системы идентификации
/// (федерация/SSO), маппящая внешнюю учётку/claims в <see cref="AccessContext"/>.
/// </summary>
/// <remarks>
/// FAIL-CLOSED (ТБ-021): если субъект не установлен, реализация обязана отказывать
/// (<see cref="AccessContextRequiredException"/>), а не выдавать «пустой»/широкий доступ.
/// </remarks>
public interface IAccessContextProvider
{
    /// <summary>Возвращает контекст доступа текущего субъекта.</summary>
    Task<AccessContext> GetCurrentAsync(CancellationToken cancellationToken = default);
}
