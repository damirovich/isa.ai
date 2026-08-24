namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт архива проверок (§5.2.4): страница групп «справка-проверка × подразделение» из учёта
/// нарушений (Э5-01). Порт — в домене, реализация — в слое данных (схема <c>inspector</c>).
/// Сами нарушения грифа не несут (реестр читают все вошедшие); решётка доступа применяется
/// отдельно — при разрешении ссылок на документы (<see cref="IInspectionDocumentResolver"/>).
/// </summary>
public interface IInspectionArchiveStore
{
    /// <summary>Страница архива с отбором; свежие проверки первыми.</summary>
    Task<ArchivePage> ListAsync(ArchiveFilter filter, CancellationToken cancellationToken = default);
}
