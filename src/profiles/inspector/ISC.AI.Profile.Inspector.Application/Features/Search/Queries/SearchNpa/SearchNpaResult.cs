namespace ISC.AI.Profile.Inspector.Application.Features.Search;

/// <summary>Результат семантического поиска по НПА.</summary>
/// <param name="Hits">Найденные фрагменты в пределах допуска (GATE-1), по умолчанию только актуальные (GATE-3).</param>
public sealed record SearchNpaResult(IReadOnlyList<NpaHit> Hits);

/// <summary>Найденный фрагмент для отображения.</summary>
/// <param name="DocumentId">Источник — идентификатор документа корпуса.</param>
/// <param name="Text">Текст фрагмента.</param>
/// <param name="Score">Релевантность (меньше — ближе).</param>
/// <param name="IsCurrent">Актуальность источника (для пометки «УТРАТИЛА СИЛУ» при показе устаревших).</param>
/// <param name="DocFlowDocumentId">
/// Если фрагмент — из документа ДОКУМЕНТООБОРОТА (поиск идёт по всему корпусу, и поручения лежат
/// рядом с НПА): идентификатор документа в документообороте, чтобы вести в его карточку, а не в
/// каталог НПА (где документооборота нет намеренно). Ключ метаданных <c>docflow_document_id</c>
/// получает смысл здесь и в чате — больше нигде.
/// </param>
public sealed record NpaHit(int DocumentId, string Text, double Score, bool IsCurrent, int? DocFlowDocumentId = null);
