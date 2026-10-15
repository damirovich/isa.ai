using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Слой пометок фотографии (ТФ-МЕД-13, ADR-0050): <c>media.annotation</c> с грифом и подразделением носителя,
/// каскадное удаление вместе с ним (ТНД-008). Решётка гриф/подразделение — на стороне БД по строке пометки и носителя;
/// сужение по делам субъекта (ТБ-071) проверяет вызывающий.
/// </summary>
public interface IMediaAnnotationStore
{
    /// <summary>Пометки носителя в порядке появления; носитель недоступен — пусто.</summary>
    Task<IReadOnlyList<AnnotationRow>> ListAsync(int assetId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Поставить пометку: гриф и подразделение — носителя. Возвращает её идентификатор; <see langword="null"/> —
    /// носитель не фото или вне допуска (неотличимо, ТБ-021).
    /// </summary>
    Task<int?> AddAsync(AnnotationDraft draft, int? authorUserId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Носитель пометки, если она доступна субъекту; иначе <see langword="null"/>.</summary>
    Task<int?> GetAssetIdAsync(int annotationId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>Снять пометку; <see langword="false"/> — её нет или она недоступна.</summary>
    Task<bool> DeleteAsync(int annotationId, AccessContext access, CancellationToken cancellationToken = default);
}
