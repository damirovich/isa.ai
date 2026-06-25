namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Запрос на экспорт документа (ТФ-ГЕН-03). Маркировка грифа <see cref="ClassificationMarking"/> —
/// ОБЯЗАТЕЛЬНА и проставляется и в теле, и в метаданных файла (ТБ-033). Текст маркировки резолвит
/// вызывающая сторона из числового грифа (режимная схема грифов — вне нейтрального ядра).
/// </summary>
/// <param name="Title">Заголовок документа.</param>
/// <param name="Body">Текст документа (абзацы разделяются переводом строки).</param>
/// <param name="ClassificationMarking">Маркировка грифа (например, «ДСП», «ОТКРЫТО»). Обязательна.</param>
/// <param name="Reference">Учётные реквизиты (номер/дата), если есть.</param>
/// <param name="DraftNotice">Пометка-предупреждение (например, HITL «ЧЕРНОВИК…»), если есть.</param>
public sealed record DocumentExportRequest(
    string Title,
    string Body,
    string ClassificationMarking,
    string? Reference = null,
    string? DraftNotice = null);
