using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Enums;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт картотеки НПА (ТФ-НПА-02): реестр норм, ведение редакций и привязка документов корпуса.
/// Порт — в домене, реализация — в слое данных (обе схемы: <c>inspector</c> — нормы,
/// <c>core</c> — корпус; связь слабыми ссылками по значению, ТО-инф-06).
/// </summary>
/// <remarks>
/// Картотека — это то, что делает механику актуальности (GATE-3) РАБОЧЕЙ: смена статуса редакции
/// гасит чанки корпуса только через связки <c>ChunkRevisionLink</c>, а до появления картотеки их
/// не создавал никто — команда смены статуса существовала, но всегда затрагивала 0 чанков.
/// </remarks>
public interface INormRegistryStore
{
    /// <summary>Страница реестра норм с отбором; новые первыми.</summary>
    Task<NormRegistryPage> ListAsync(NormListFilter filter, CancellationToken cancellationToken = default);

    /// <summary>
    /// Карточка нормы; <see langword="null"/> — не найдена. Реквизиты привязанных документов корпуса
    /// (заголовок, тип, гриф) — только в пределах решётки допуска субъекта <paramref name="access"/>
    /// (ТБ-020/021): вне допуска связка отдаётся как «документ №N» без реквизитов.
    /// </summary>
    Task<NormDetails?> GetAsync(int normId, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Создаёт норму. Номер (<paramref name="identifier"/>) уникален —
    /// <see cref="NormWriteResult.DuplicateIdentifier"/> при повторе: на номер ссылается грунтовка,
    /// и две нормы с одним номером сделали бы ссылку неоднозначной.
    /// </summary>
    Task<(NormWriteResult Result, int NormId)> CreateAsync(
        string identifier, string title, CancellationToken cancellationToken = default);

    /// <summary>Правит номер и название нормы (та же проверка уникальности номера).</summary>
    Task<NormWriteResult> UpdateAsync(
        int normId, string identifier, string title, CancellationToken cancellationToken = default);

    /// <summary>
    /// Добавляет редакцию нормы. Новая редакция создаётся со статусом «Действующая»; проставить
    /// «Утратила силу» прежней — отдельная операция смены статуса (она же гасит чанки, GATE-3),
    /// автоматического гашения здесь НЕТ намеренно: решение о судьбе прежней редакции — за оператором.
    /// </summary>
    Task<(NormWriteResult Result, int RevisionId)> AddRevisionAsync(
        int normId, DateOnly effectiveDate, CancellationToken cancellationToken = default);

    /// <summary>
    /// Привязывает документ корпуса к норме и ВСЕ его чанки — к редакции
    /// (<c>NormDocumentLink</c> + <c>ChunkRevisionLink</c>). Возвращает число привязанных чанков.
    /// </summary>
    /// <remarks>
    /// Гранула связки — редакция↔чанк: именно её читает материализатор статуса. Привязываются все
    /// чанки документа (структурной разметки «чанк ↔ статья» конвейер не даёт — контракт чанкера
    /// возвращает голые строки). Отказы: повтор документа у той же нормы —
    /// <see cref="NormWriteResult.AlreadyLinked"/>; документ погашен заменой (supersede) или без
    /// единого чанка — <see cref="NormWriteResult.NotFound"/> (мёртвая связка ничего не даёт,
    /// а отвязки в картотеке нет). Привязка к утратившей силу редакции ГАСИТ чанки немедленно
    /// (hide-first, GATE-3); видимость привязка не поднимает никогда — только явная смена статуса.
    /// </remarks>
    Task<(NormWriteResult Result, int LinkedChunks)> LinkDocumentAsync(
        int normId, int revisionId, int coreDocumentId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Документы корпуса — кандидаты на привязку: отбор по заголовку/типу; уже привязанные к этой
    /// норме, погашенные заменой и пустые (без чанков) исключены. Решётка допуска субъекта
    /// <paramref name="access"/> — В ЗАПРОСЕ (ТБ-020/021): предлагются только документы,
    /// которые субъект и так вправе видеть.
    /// </summary>
    Task<IReadOnlyList<CorpusDocumentCandidate>> SearchCorpusDocumentsAsync(
        int normId, string? text, AccessContext access, CancellationToken cancellationToken = default);
}
