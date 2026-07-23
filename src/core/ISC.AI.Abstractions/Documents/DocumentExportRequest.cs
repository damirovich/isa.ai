namespace ISC.AI.Abstractions.Documents;

/// <summary>
/// Запрос на экспорт документа (ТФ-ГЕН-03). Учётные реквизиты ТБ-033 — гриф
/// (<see cref="ClassificationMarking"/>), учётный номер (<see cref="Reference"/>) и исполнитель
/// (<see cref="Executor"/>) — проставляются экспортёром И в теле, И в метаданных файла. Отсутствующее
/// значение маркируется явным плейсхолдером («не присвоен»/«не указан»), чтобы маркировка была структурно
/// полной. Текст маркировки грифа резолвит вызывающая сторона из числового грифа (режимная схема — вне ядра).
/// </summary>
/// <param name="Title">Заголовок документа.</param>
/// <param name="Body">Текст документа (абзацы разделяются переводом строки).</param>
/// <param name="ClassificationMarking">Маркировка грифа (например, «ДСП», «ОТКРЫТО»). Обязательна.</param>
/// <param name="Reference">Учётный номер (регистрационный номер/дата). <see langword="null"/> — «не присвоен» (ТБ-033).</param>
/// <param name="Executor">Исполнитель (кто изготовил документ). <see langword="null"/> — «не указан» (ТБ-033).</param>
/// <param name="DraftNotice">Пометка-предупреждение (например, HITL «ЧЕРНОВИК…»), если есть.</param>
public sealed record DocumentExportRequest(
    string Title,
    string Body,
    string ClassificationMarking,
    string? Reference = null,
    string? Executor = null,
    string? DraftNotice = null);
