using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Дело, по материалам которого идёт чат: номер и название для подписи, гриф и подразделение — для журнала.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Number">Номер дела.</param>
/// <param name="Classification">Гриф дела.</param>
/// <param name="DivisionId">Подразделение дела.</param>
/// <param name="Title">Название дела.</param>
public sealed record CaseChatScope(int CaseId, string Number, short Classification, int DivisionId, string Title = "");

/// <summary>Вид материала дела в чате (ADR-0051).</summary>
public enum ChatSourceKind
{
    /// <summary>Документ документооборота, прикреплённый к делу.</summary>
    Document = 1,

    /// <summary>Сводка или справка по бланку.</summary>
    Report = 2,

    /// <summary>Расшифровка записи (аудио, звуковая дорожка видео).</summary>
    Transcript = 3,

    /// <summary>«Сведения дела»: реквизиты, фигуранты, адреса, транспорт.</summary>
    CaseCard = 4,
}

/// <summary>
/// Дела, по которым сотрудник может спрашивать в чате (ADR-0051/0052): области чата профиля. Переписку ведёт пакет
/// «Чат» в таблицах ядра; профилю остаётся решить, какие дела видны.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ДОСТУПА (ТБ-020/021/071): дело выдаётся только при видимости по полной решётке профиля
/// (<c>CaseAccessRule.Apply</c>); без права «Дашборд и реестр дел» не видно ни одного. Невидимое и несуществующее дело
/// неразличимы (<see langword="null"/>).
/// </remarks>
public interface ICaseChatScopes
{
    /// <summary>Дело для чата, если оно видно субъекту; иначе <see langword="null"/>.</summary>
    Task<CaseChatScope?> GetScopeAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Все видимые субъекту дела (для выбора области и для области «все мои дела»), новые сверху.</summary>
    Task<IReadOnlyList<CaseChatScope>> ListScopesAsync(AccessContext access, CancellationToken cancellationToken = default);
}
