using ISC.AI.Abstractions.Security;

namespace ISC.AI.Profile.Inspector.Domain.Services;

/// <summary>
/// Порт каталога корпуса НПА (ТФ-НПА-01/02): реестр документов корпуса со статусом действия
/// и карточка документа с текстом. Дополняет семантический поиск (<c>IRetriever</c>): поиск отвечает
/// на вопрос «где про это сказано», каталог — «какие документы вообще есть и действуют ли».
/// </summary>
/// <remarks>
/// Решётка допуска — В ЗАПРОСЕ (ТБ-020/021): выдаются только документы с грифом не выше допуска
/// субъекта и из разрешённых ему подразделений; недоступный документ неотличим от несуществующего.
/// Документы документооборота (<c>CorpusSources.DocFlow</c>) в каталог НПА не попадают — у них свой
/// реестр. Статус действия — ТОЛЬКО то, что знает система (флаг годности чанков, GATE-3): «утратил
/// силу» появляется по картотеке или гашению при замене версии, не из внешнего источника.
/// </remarks>
public interface ICorpusCatalog
{
    /// <summary>Страница каталога с отбором и сортировкой; виды актов — для фильтра.</summary>
    Task<CorpusCatalogPage> ListAsync(
        CorpusCatalogFilter filter, AccessContext access, CancellationToken cancellationToken = default);

    /// <summary>
    /// Карточка документа с текстом по порядку фрагментов; <see langword="null"/> — не найден ИЛИ
    /// вне допуска (та же неразличимость, что у карточки документооборота).
    /// </summary>
    Task<CorpusDocumentDetails?> GetAsync(
        int documentId, AccessContext access, CancellationToken cancellationToken = default);
}
