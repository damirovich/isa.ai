using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Тип документа, допустимый для документов архива (группа «Хранение» у «Следствия»).</summary>
/// <param name="Id">Идентификатор типа.</param>
/// <param name="Name">Наименование — по нему опись указывает тип.</param>
public sealed record ArchiveDocumentType(int Id, string Name);

/// <summary>Документ архива для регистрации и привязки к делу (ADR-0042).</summary>
/// <param name="Case">Дело, уже проверенное на доступность оператору: гриф и подразделение документа — его (ТБ-070).</param>
/// <param name="FileName">Исходное имя файла.</param>
/// <param name="ContentType">MIME-тип по расширению (сигнатура уже сверена).</param>
/// <param name="Content">Содержимое файла.</param>
/// <param name="TypeId">Тип документа.</param>
/// <param name="RegNumber">Регистрационный номер или <see langword="null"/>.</param>
/// <param name="RegDate">Дата документа.</param>
/// <param name="Title">Краткое содержание.</param>
/// <param name="Source">Источник (откуда документ).</param>
/// <param name="Notes">Примечание: задание и строка импорта — происхождение (ТБ-077).</param>
/// <param name="ExistingDocumentId">Документ, созданный прежней попыткой этой же строки, — продолжить его.</param>
public sealed record ArchiveDocumentRequest(
    CaseScopeItem Case,
    string FileName,
    string ContentType,
    byte[] Content,
    int TypeId,
    string? RegNumber,
    DateOnly RegDate,
    string Title,
    string? Source,
    string Notes,
    int? ExistingDocumentId);

/// <summary>Итог регистрации документа архива.</summary>
/// <param name="DocumentId">Документ; <see langword="null"/> — отказ.</param>
/// <param name="Error">Причина отказа для отчёта импорта.</param>
public sealed record ArchiveDocumentResult(int? DocumentId, string? Error)
{
    /// <summary>Отказ с причиной.</summary>
    public static ArchiveDocumentResult Fail(string error) => new(null, error);
}

/// <summary>
/// ПОРТ ПРОФИЛЯ (ADR-0042): куда кладутся документы архива. Пакет «Медиа» о документообороте не знает (пакеты друг на
/// друга не ссылаются, ADR-0017): профиль регистрирует документ в своём документообороте и привязывает его к делу.
/// Без профиля с документооборотом действует поставка по умолчанию — «документы этот экземпляр не принимает».
/// </summary>
/// <remarks>
/// ИНВАРИАНТЫ: гриф и подразделение документа — дела (ТБ-070), и регистрация идёт под допуском оператора
/// (<see cref="AccessContext"/>) с той же решёткой, что у ручной регистрации; файл и привязка к делу — тем же
/// сценарием. Документ создаётся и сразу сообщается вызывающему (<c>onCreated</c>) — до загрузки файла и привязки, чтобы
/// прерванный приём не заводил второй документ при повторе.
/// </remarks>
public interface IArchiveDocumentSink
{
    /// <summary>Экземпляр принимает документы архива.</summary>
    bool IsSupported { get; }

    /// <summary>Типы, допустимые для документов архива.</summary>
    Task<IReadOnlyList<ArchiveDocumentType>> ListTypesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Регистрирует документ (или продолжает созданный прежней попыткой), загружает файл и привязывает к делу.
    /// Индексацию текста НЕ ставит — её подаёт задание порциями (<see cref="EnqueueIndexingAsync"/>).
    /// </summary>
    /// <param name="request">Документ.</param>
    /// <param name="access">Допуск оператора задания.</param>
    /// <param name="onCreated">Вызывается сразу после создания документа с его идентификатором.</param>
    /// <param name="cancellationToken">Отмена.</param>
    Task<ArchiveDocumentResult> ImportAsync(
        ArchiveDocumentRequest request, AccessContext access, Func<int, Task> onCreated, CancellationToken cancellationToken = default);

    /// <summary>Ставит индексацию текста документа в общую фоновую очередь; возвращает идентификатор задачи.</summary>
    Task<Guid> EnqueueIndexingAsync(int documentId, CancellationToken cancellationToken = default);
}
