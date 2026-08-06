using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Services;

/// <summary>Упомянутый участник в ленте комментариев.</summary>
public sealed record CommentMentionItem(int UserId, string Name);

/// <summary>Файл комментария в ленте (ссылка строится по <see cref="StoredFileName"/>, категория <c>comments</c>).</summary>
public sealed record CommentFileItem(int Id, string FileName, string ContentType, long FileSize, string StoredFileName);

/// <summary>
/// Комментарий в ленте (§4.8). У удалённого (<see cref="IsDeleted"/>) содержимое обнуляется НА СЕРВЕРЕ:
/// <see cref="Content"/> пуст, файлов и упоминаний нет — UI лишь показывает заглушку (перенос контракта СКИД).
/// </summary>
public sealed record CommentItem(
    int Id,
    int? ParentCommentId,
    int AuthorUserId,
    string AuthorName,
    string Content,
    CommentType CommentType,
    bool IsResolved,
    int? ResolvedByUserId,
    DateTime? ResolvedAt,
    bool IsEdited,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime? UpdatedAt,
    IReadOnlyList<CommentMentionItem> Mentions,
    IReadOnlyList<CommentFileItem> Files);

/// <summary>Черновик комментария: текст, вид, заявленные клиентом упоминания и файлы.</summary>
public sealed record CommentDraft(
    int DocumentId,
    int? ParentCommentId,
    string Content,
    CommentType CommentType,
    IReadOnlyList<int> MentionedUserIds,
    IReadOnlyList<UploadedFile>? Files);

/// <summary>Итог записи комментария (детализирует отказ — тексты сообщений формирует слой сценариев).</summary>
public enum CommentWriteStatus
{
    /// <summary>Выполнено.</summary>
    Ok,

    /// <summary>Документ либо комментарий не найден (или недоступен — наружу не различается).</summary>
    NotFound,

    /// <summary>Родительский комментарий принадлежит ДРУГОМУ документу (подмена маршрута).</summary>
    ParentMismatch,

    /// <summary>Действие доступно только автору комментария.</summary>
    NotAuthor,

    /// <summary>Комментарий уже удалён — правка невозможна.</summary>
    AlreadyDeleted,

    /// <summary>Закрывать/переоткрывать можно только КОРНЕВОЕ обсуждение (§4.8, решение СКИД DL-077).</summary>
    OnlyRootCanBeResolved,

    /// <summary>Конкурентное изменение — повторить с актуальными данными.</summary>
    Conflict,
}

/// <summary>
/// Порт комментариев к документу (ТЗ СКИД §4.8). Проверку допуска к САМОМУ документу выполняет
/// вызывающий сценарий (решётка гриф/подразделение + построчная политика роли) — порт работает уже
/// с проверенным идентификатором документа.
/// </summary>
public interface ICommentStore
{
    /// <summary>Лента комментариев документа в хронологическом порядке (ответы — тем же списком, по <c>ParentCommentId</c>).</summary>
    Task<IReadOnlyList<CommentItem>> ListAsync(int documentId, CancellationToken cancellationToken = default);

    /// <summary>Добавляет комментарий (при <c>ParentCommentId</c> — ответ). Возвращает статус и идентификатор.</summary>
    Task<(CommentWriteStatus Status, int CommentId)> AddAsync(
        CommentDraft draft, int authorUserId, CancellationToken cancellationToken = default);

    /// <summary>Правит текст и упоминания своего комментария (файлы и вид неизменяемы — как в СКИД).</summary>
    Task<CommentWriteStatus> UpdateAsync(
        int commentId, string content, IReadOnlyList<int> mentionedUserIds, int editorUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Мягко удаляет свой комментарий; повторный вызов идемпотентен (как в СКИД).</summary>
    Task<CommentWriteStatus> DeleteAsync(int commentId, int actorUserId, CancellationToken cancellationToken = default);

    /// <summary>Закрывает/переоткрывает КОРНЕВОЕ обсуждение; идемпотентно.</summary>
    Task<CommentWriteStatus> SetResolvedAsync(
        int commentId, bool resolved, int actorUserId, CancellationToken cancellationToken = default);
}
