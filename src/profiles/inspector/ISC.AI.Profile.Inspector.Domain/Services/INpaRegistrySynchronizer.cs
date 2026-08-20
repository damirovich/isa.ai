namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>Итог синхронизации картотеки с корпусом (счётчики — для отчёта оператору и аудита).</summary>
public sealed record NpaSyncResult(
    int Scanned,
    int NormsCreated,
    int RevisionsCreated,
    int DocumentsLinked,
    int ChunksLinked,
    int RevisionsRepealed);

/// <summary>
/// Автонаполнение картотеки НПА из корпуса (закрытие «Осталось» Э4-02): документы, загруженные
/// из пакетов ЦБД Минюста, несут в метаданных номер акта (<c>documentCode</c>) и номер редакции
/// (<c>editionId</c>) — по ним создаются нормы, редакции и связки с чанками, которые до этого
/// администратор заводил руками.
/// </summary>
/// <remarks>
/// Правила (осознанные):
/// <list type="number">
/// <item>Ключ нормы — <c>documentCode</c> ЦБД: он стабилен между редакциями акта (человеческий
/// «№ 135» — нет: повторяется у разных актов разных лет).</item>
/// <item>Ключ редакции — <c>editionId</c> (хранится в <c>NormRevision.ExternalEditionId</c>);
/// документ без <c>editionId</c> получает свой ключ по номеру документа корпуса — идемпотентно.</item>
/// <item>Приход НОВОГО <c>editionId</c> того же акта ГАСИТ прежние автоматические редакции нормы
/// (через материализатор — чанки скрываются, GATE-3): ЦБД отдаёт только действующие, значит
/// прежняя редакция утратила силу. РУЧНЫЕ редакции (без внешнего ключа) не трогаются никогда —
/// решение о них принял человек.</item>
/// <item>Идемпотентность: повторный запуск ничего не меняет (кандидаты — только документы без
/// связки с картотекой); документы документооборота и погашенные заменой не рассматриваются.</item>
/// </list>
/// </remarks>
public interface INpaRegistrySynchronizer
{
    /// <summary>Проходит корпус и достраивает картотеку; возвращает счётчики сделанного.</summary>
    Task<NpaSyncResult> SyncFromCorpusAsync(CancellationToken cancellationToken = default);
}
