using ISC.AI.Abstractions.Entities;
using ISC.AI.Profile.Investigation.Domain.Enums;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>Привязка носителя к делу (<c>investigation.case_media_link</c>, ТФ-ДЕЛ-02): слабая ссылка в схему <c>media</c> (ТО-инф-08).</summary>
public class CaseMediaLink : BaseEntity
{
    /// <summary>Дело (FK внутри схемы).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Носитель (по значению → media.asset).</summary>
    public int MediaAssetId { get; set; }

    /// <summary>Место съёмки/изъятия (ТФ-МЕД-01) — реквизит «на дело».</summary>
    public string? Place { get; set; }

    /// <summary>Кто привязал (слабая ссылка).</summary>
    public int? LinkedByUserId { get; set; }
}

/// <summary>Привязка документа документооборота к делу (<c>investigation.case_document_link</c>, ТФ-ДДЛ-01): слабая ссылка в схему <c>docflow</c>.</summary>
public class CaseDocumentLink : BaseEntity
{
    /// <summary>Дело (FK внутри схемы).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Документ (по значению → docflow.document).</summary>
    public int DocFlowDocumentId { get; set; }

    /// <summary>Кто привязал (слабая ссылка).</summary>
    public int? LinkedByUserId { get; set; }
}

/// <summary>
/// Основание поиска по лицу (<c>investigation.search_authorization</c>, ТБ-071): поручение следователя,
/// постановление или номер ОРМ. Поиск без ссылки на основание технически невозможен.
/// </summary>
public class SearchAuthorization : AuditableEntity
{
    /// <summary>Дело (FK внутри схемы).</summary>
    public int CaseId { get; set; }

    /// <summary>Навигация к делу.</summary>
    public CaseFile? Case { get; set; }

    /// <summary>Вид основания.</summary>
    public AuthorizationKind Kind { get; set; }

    /// <summary>Реквизиты (номер, дата документа) — строка, которую поиск указывает в аудите.</summary>
    public required string Reference { get; set; }

    /// <summary>Дата выдачи.</summary>
    public DateOnly IssuedAt { get; set; }

    /// <summary>Кто выдал/внёс (слабая ссылка).</summary>
    public int? IssuedByUserId { get; set; }

    /// <summary>Действует до (если ограничено).</summary>
    public DateOnly? ValidUntil { get; set; }

    /// <summary>Примечание.</summary>
    public string? Notes { get; set; }
}

/// <summary>Подразделение профиля (<c>investigation.division</c>): словарь идентификаторов для допусков ядра (<c>core.clearance.division_scope</c>).</summary>
public class Division : AuditableEntity
{
    /// <summary>Наименование.</summary>
    public required string Name { get; set; }

    /// <summary>Код (необязателен).</summary>
    public string? Code { get; set; }

    /// <summary>Родительское подразделение (иерархия), если есть.</summary>
    public int? ParentId { get; set; }

    /// <summary>Действующее.</summary>
    public bool IsActive { get; set; } = true;
}

/// <summary>Назначение роли пользователю (<c>investigation.user_role_assignment</c>, ТП-004): одна роль на пользователя, слабая ссылка на <c>core.app_user</c>.</summary>
public class UserRoleAssignment : AuditableEntity
{
    /// <summary>Пользователь ядра.</summary>
    public int UserId { get; set; }

    /// <summary>Роль.</summary>
    public InvestigationRole Role { get; set; }
}
