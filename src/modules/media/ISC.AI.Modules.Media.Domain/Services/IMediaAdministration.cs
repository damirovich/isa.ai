namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// ПОРТ ПРОФИЛЯ: кто вправе загружать носители, искать по лицу и удалять (ТП-004). Право определяется
/// РОЛЬЮ текущего субъекта, а роли ведёт профиль. Решётка допуска (гриф/подразделение) — отдельно и
/// всегда (ТБ-020); эти проверки — поверх неё.
/// </summary>
public interface IMediaAdministration
{
    /// <summary>Текущий субъект вправе загружать носители в дела.</summary>
    Task<bool> CanUploadAsync(CancellationToken cancellationToken = default);

    /// <summary>Текущий субъект вправе инициировать поиск по лицу.</summary>
    Task<bool> CanSearchAsync(CancellationToken cancellationToken = default);

    /// <summary>Текущий субъект вправе гарантированно удалять носители (ТБ-074).</summary>
    Task<bool> CanPurgeAsync(CancellationToken cancellationToken = default);

    /// <summary>Текущий субъект вправе импортировать архив материалов с диска сервера по описи (ADR-0041).</summary>
    Task<bool> CanImportArchiveAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Пользователь вправе импортировать архив — для фонового задания, которое идёт без его сессии (ADR-0041): право
    /// проверяется перед КАЖДЫМ файлом, и снятая роль останавливает задание так же немедленно, как живую работу.
    /// </summary>
    /// <param name="userId">Пользователь, запустивший задание.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<bool> UserCanImportArchiveAsync(int userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Текущий субъект вправе менять настройки поиска и верификации (ADR-0054): режим подтверждения, рабочий порог и
    /// нижнюю границу порога. Это правила безопасности поиска по лицу — по ТЗ их задаёт Администратор (ТФ-ВЕР-07, ТФ-ПЛ-08).
    /// </summary>
    Task<bool> CanManageSettingsAsync(CancellationToken cancellationToken = default);
}
