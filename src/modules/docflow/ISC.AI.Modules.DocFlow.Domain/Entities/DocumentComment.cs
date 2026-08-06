using ISC.AI.Abstractions.Entities;
using ISC.AI.Modules.DocFlow.Domain.Enums;

namespace ISC.AI.Modules.DocFlow.Domain.Entities;

/// <summary>
/// Комментарий к документу (ТЗ СКИД §4.8): обсуждение с ответами (self-FK), упоминаниями участников
/// и файлами. Мягкое удаление (<see cref="ISC.AI.Abstractions.Entities.ISoftDeletable.IsDeleted"/>) —
/// перенос решения СКИД: строка остаётся ради связности ветки ответов и следа в аудите, но содержимое
/// наружу не отдаётся (обнуляется на СЕРВЕРЕ, не только скрывается в UI).
/// </summary>
public class DocumentComment : SoftDeletableEntity
{
    /// <summary>Документ (FK внутри схемы).</summary>
    public int DocumentId { get; set; }

    /// <summary>Автор — слабая ссылка на <c>core.app_user</c> (ТО-инф-06).</summary>
    public int AuthorUserId { get; set; }

    /// <summary>Родительский комментарий; <see langword="null"/> — корневой (ответ на документ).</summary>
    public int? ParentCommentId { get; set; }

    /// <summary>Текст в разметке Markdown (санитизация — при рендере, не при хранении).</summary>
    public required string Content { get; set; }

    /// <summary>Вид комментария (§4.8).</summary>
    public CommentType CommentType { get; set; }

    /// <summary>Обсуждение закрыто (только для корневого — §4.8, решение СКИД DL-077).</summary>
    public bool IsResolved { get; set; }

    /// <summary>Кто закрыл обсуждение — слабая ссылка на <c>core.app_user</c>.</summary>
    public int? ResolvedByUserId { get; set; }

    /// <summary>Когда закрыто.</summary>
    public DateTime? ResolvedAt { get; set; }

    /// <summary>Текст правился после публикации (метка «изменено»; истории правок нет — как в СКИД).</summary>
    public bool IsEdited { get; set; }

    /// <summary>Упоминания участников в этом комментарии.</summary>
    public ICollection<DocumentCommentMention> Mentions { get; set; } = [];

    /// <summary>Файлы, приложенные к комментарию.</summary>
    public ICollection<DocumentCommentFile> Files { get; set; } = [];
}
