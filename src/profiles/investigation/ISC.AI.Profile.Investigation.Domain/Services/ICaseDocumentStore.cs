using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Привязка документа документооборота к делу (ТФ-ДЕЛ-02, ТФ-ДДЛ-01).</summary>
/// <param name="DocumentId">Документ модуля документооборота (по значению).</param>
/// <param name="LinkedByUserId">Кто прикрепил.</param>
/// <param name="LinkedAt">Когда прикреплён (UTC).</param>
public sealed record CaseDocumentLinkRow(int DocumentId, int? LinkedByUserId, DateTime LinkedAt);

/// <summary>Исход прикрепления или открепления документа.</summary>
public enum CaseDocumentWriteResult
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Дело не найдено или недоступно (ТБ-021).</summary>
    NotFound = 1,

    /// <summary>Документ уже прикреплён к этому делу.</summary>
    AlreadyLinked = 2,

    /// <summary>Документ не прикреплён к этому делу.</summary>
    NotLinked = 3,
}

/// <summary>
/// Документы дела (<c>investigation.case_document_link</c>, ТФ-ДЕЛ-02): связь «дело ↔ документ документооборота»
/// по значению идентификатора — FK через границу схем нет (ТО-инф-08). Хранилище отвечает ТОЛЬКО за связь и
/// видимость дела (полная решётка с ролью, <c>CaseAccessRule</c>); видимость самого документа проверяет
/// вызывающий через порт документооборота — документ вне допуска субъекту не показывается и не прикрепляется.
/// </summary>
public interface ICaseDocumentStore
{
    /// <summary>Привязки дела, новые первыми; <see langword="null"/> — дело недоступно.</summary>
    Task<IReadOnlyList<CaseDocumentLinkRow>?> ListAsync(int caseId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Прикрепить документ к делу.</summary>
    Task<CaseDocumentWriteResult> AttachAsync(int caseId, int documentId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Открепить документ от дела (сам документ не удаляется).</summary>
    Task<CaseDocumentWriteResult> DetachAsync(int caseId, int documentId, AccessContext access, CancellationToken cancellationToken = default);
}
