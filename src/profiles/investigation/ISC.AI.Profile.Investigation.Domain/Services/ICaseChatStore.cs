using System.Globalization;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Пределы чата по документам дела (ADR-0051).</summary>
public static class CaseChatLimits
{
    /// <summary>Наибольшая длина заголовка диалога.</summary>
    public const int MaxTitleLength = 200;

    /// <summary>Наибольшая длина вопроса.</summary>
    public const int MaxQuestionLength = 4_000;

    /// <summary>Наибольшая длина хранимого ответа (длиннее — обрезается с пометкой).</summary>
    public const int MaxAnswerLength = 32_000;

    /// <summary>Наибольшая длина выдержки из фрагмента-источника, хранимой при ответе.</summary>
    public const int MaxExcerptLength = 400;
}

/// <summary>Дело, по материалам которого идёт чат: номер и название для подписи, гриф и подразделение — для журнала.</summary>
/// <param name="CaseId">Дело.</param>
/// <param name="Number">Номер дела.</param>
/// <param name="Classification">Гриф дела.</param>
/// <param name="DivisionId">Подразделение дела.</param>
/// <param name="Title">Название дела.</param>
public sealed record CaseChatScope(int CaseId, string Number, short Classification, int DivisionId, string Title = "");

/// <summary>Вид источника ответа чата (ADR-0051).</summary>
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

/// <summary>Диалог в списке вкладки.</summary>
/// <param name="Id">Диалог.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Classification">Гриф диалога (для маркировки).</param>
/// <param name="UpdatedAt">Последняя активность (UTC).</param>
public sealed record CaseChatSummary(int Id, string Title, short Classification, DateTime UpdatedAt);

/// <summary>
/// Источник ответа: фрагмент материала дела, поданный модели под номером <see cref="Number"/> («[Фрагмент N]»).
/// </summary>
/// <param name="Number">Номер фрагмента в запросе к модели (с 1) — по нему ответ ссылается на источник.</param>
/// <param name="Kind">Вид материала.</param>
/// <param name="TargetId">Что открыть: документ документооборота, сводку, носитель или дело — по виду.</param>
/// <param name="CaseId">Дело, к которому относится материал (уничтожение дела уносит ответы, опиравшиеся на него).</param>
/// <param name="Label">Подпись источника.</param>
/// <param name="ChunkId">Фрагмент корпуса ядра (<c>core.chunk.id</c>) — для разбора по журналу.</param>
/// <param name="Classification">Гриф фрагмента.</param>
/// <param name="Excerpt">Начало текста фрагмента (не длиннее <see cref="CaseChatLimits.MaxExcerptLength"/>).</param>
/// <param name="StartMs">Для расшифровки — момент записи, с которого начинается фрагмент, мс.</param>
public sealed record CaseChatSource(
    int Number, ChatSourceKind Kind, int TargetId, int CaseId, string Label, int ChunkId, short Classification, string Excerpt, long? StartMs = null)
{
    /// <summary>Адрес источника в интерфейсе: карточка документа, сводки, дела или носитель на нужном моменте.</summary>
    public string Href => Kind switch
    {
        ChatSourceKind.Document => "/docflow/documents/" + TargetId.ToString(CultureInfo.InvariantCulture),
        ChatSourceKind.Report => "/reports/" + TargetId.ToString(CultureInfo.InvariantCulture),
        ChatSourceKind.Transcript => "/media/assets/" + TargetId.ToString(CultureInfo.InvariantCulture)
            + (StartMs is { } ms ? "?t=" + ms.ToString(CultureInfo.InvariantCulture) : string.Empty),
        _ => "/cases/" + TargetId.ToString(CultureInfo.InvariantCulture),
    };
}

/// <summary>Итог проверки ссылок ответа (ТБ-040, ADR-0051).</summary>
/// <param name="Unverified">Ссылки ответа (номера, статьи), которых нет в поданных фрагментах, — «по памяти» модели.</param>
/// <param name="UnknownFragments">Номера «[Фрагмент N]», которых модели не подавали.</param>
public sealed record CaseChatCheck(IReadOnlyList<string> Unverified, IReadOnlyList<int> UnknownFragments)
{
    /// <summary>Все ссылки ответа подтверждены поданными фрагментами.</summary>
    public bool AllConfirmed => Unverified.Count == 0 && UnknownFragments.Count == 0;
}

/// <summary>Сообщение диалога для показа.</summary>
/// <param name="Id">Сообщение.</param>
/// <param name="Role">Автор.</param>
/// <param name="Text">Текст.</param>
/// <param name="Classification">Гриф.</param>
/// <param name="CreatedAt">Когда (UTC).</param>
/// <param name="Sources">Источники ответа (у вопроса — пусто).</param>
/// <param name="Check">Итог проверки ссылок ответа (у вопроса — <see langword="null"/>).</param>
public sealed record CaseChatMessageRow(
    int Id,
    ConversationMessageRole Role,
    string Text,
    short Classification,
    DateTime CreatedAt,
    IReadOnlyList<CaseChatSource> Sources,
    CaseChatCheck? Check);

/// <summary>Диалог с сообщениями.</summary>
/// <param name="Id">Диалог.</param>
/// <param name="CaseId">Дело или <see langword="null"/> — диалог по всем доступным делам.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Classification">Гриф диалога.</param>
/// <param name="Messages">Сообщения в пределах текущего допуска, по порядку.</param>
/// <param name="HiddenMessages">Сколько сообщений скрыто: их гриф выше текущего допуска (допуск понизили, ТБ-020).</param>
public sealed record CaseChatThread(
    int Id, int? CaseId, string Title, short Classification, IReadOnlyList<CaseChatMessageRow> Messages, int HiddenMessages);

/// <summary>Новое сообщение диалога.</summary>
/// <param name="Role">Автор.</param>
/// <param name="Text">Текст.</param>
/// <param name="Classification">Гриф.</param>
/// <param name="Sources">Источники ответа.</param>
/// <param name="Check">Итог проверки ссылок ответа.</param>
public sealed record CaseChatMessageDraft(
    ConversationMessageRole Role,
    string Text,
    short Classification,
    IReadOnlyList<CaseChatSource>? Sources = null,
    CaseChatCheck? Check = null);

/// <summary>
/// Диалоги чата по материалам дел (<c>investigation.case_chat</c>, ADR-0051).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ДОСТУПА (ТБ-020/021/071). Каждая операция — только над СВОИМ диалогом; у диалога по делу — ещё и при
/// видимости дела по полной решётке профиля (<c>CaseAccessRule.Apply</c>). Чужой диалог, диалог дела вне доступа и
/// несуществующий неразличимы (<see langword="null"/> или <see langword="false"/>). Без числового идентификатора
/// сотрудника — ничего (диалог не к кому привязать). Сообщения с грифом выше ТЕКУЩЕГО допуска не выдаются, только их
/// число. <c>caseId = null</c> — диалоги по всем доступным делам.
/// </remarks>
public interface ICaseChatStore
{
    /// <summary>Дело для чата, если оно видно субъекту; иначе <see langword="null"/>.</summary>
    Task<CaseChatScope?> GetScopeAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Все видимые субъекту дела (для выбора области и для области «все мои дела»), новые сверху.</summary>
    Task<IReadOnlyList<CaseChatScope>> ListScopesAsync(AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Свои диалоги по делу (или по всем делам при <c>null</c>), новые сверху; <see langword="null"/> — дело не видно.</summary>
    Task<IReadOnlyList<CaseChatSummary>?> ListAsync(int? caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Новый диалог по делу (или по всем делам при <c>null</c>); <see langword="null"/> — дело не видно или сотрудник не определён.</summary>
    Task<int?> CreateAsync(int? caseId, string title, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Свой диалог с сообщениями; <see langword="null"/> — нет, чужой или дело не видно.</summary>
    Task<CaseChatThread?> GetAsync(int chatId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Добавить сообщение в свой диалог (гриф диалога поднимается до грифа сообщения).</summary>
    Task<bool> AppendAsync(int chatId, CaseChatMessageDraft message, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Переименовать свой диалог.</summary>
    Task<bool> RenameAsync(int chatId, string title, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Удалить свой диалог физически (журнал аудита при этом не меняется).</summary>
    Task<bool> DeleteAsync(int chatId, AccessContext access, CancellationToken cancellationToken = default);
}
