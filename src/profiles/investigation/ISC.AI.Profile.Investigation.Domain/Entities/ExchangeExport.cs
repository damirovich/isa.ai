using ISC.AI.Abstractions.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Entities;

/// <summary>Состояние выгрузки пакета обмена (ADR-0068).</summary>
public enum ExchangeExportStatus : short
{
    /// <summary>Пакет формируется.</summary>
    Running = 1,

    /// <summary>Пакет сформирован и лежит на носителе.</summary>
    Succeeded = 2,

    /// <summary>Сбой: пакета нет (каталог «.partial» удалён).</summary>
    Failed = 3,

    /// <summary>Хост перезапустился посреди формирования: пакета нет.</summary>
    Interrupted = 4,
}

/// <summary>
/// Выгрузка пакета обмена между офисами (<c>investigation.exchange_export</c>, ТФ-ОБМ-01, ADR-0068): кто, когда, за какой
/// период, по какому отбору, итог, имя пакета, счётчики, объём и маркировка. Это журнал работы, а не содержимое: пароль
/// ключа подписи сюда не попадает никогда.
/// </summary>
public class ExchangeExport : BaseEntity
{
    /// <summary>Когда начата.</summary>
    public DateTime StartedAt { get; set; }

    /// <summary>Когда закончена (успешно или нет).</summary>
    public DateTime? FinishedAt { get; set; }

    /// <summary>Состояние.</summary>
    public ExchangeExportStatus Status { get; set; }

    /// <summary>Кто начал (номер учётной записи).</summary>
    public int StartedByUserId { get; set; }

    /// <summary>Год периода.</summary>
    public int Year { get; set; }

    /// <summary>Месяц периода.</summary>
    public int Month { get; set; }

    /// <summary>Номера дел из отбора через запятую; пусто — дела с движением за период.</summary>
    public string? CaseNumbers { get; set; }

    /// <summary>Виды носителей из отбора через запятую (имена: Image, Video, Audio).</summary>
    public required string MediaKinds { get; set; }

    /// <summary>Имя каталога пакета на носителе (после успеха).</summary>
    public string? PackageName { get; set; }

    /// <summary>Дел в пакете.</summary>
    public int Cases { get; set; }

    /// <summary>Объектов в пакете.</summary>
    public int Persons { get; set; }

    /// <summary>Связей в пакете (внутри дел и события связи).</summary>
    public int Links { get; set; }

    /// <summary>Сводок и справок в пакете.</summary>
    public int Reports { get; set; }

    /// <summary>Носителей в пакете (с файлом и только реквизитами).</summary>
    public int Assets { get; set; }

    /// <summary>Файлов носителей в пакете.</summary>
    public int Files { get; set; }

    /// <summary>Появлений в пакете.</summary>
    public int Appearances { get; set; }

    /// <summary>Записано на носитель, байт.</summary>
    public long BytesWritten { get; set; }

    /// <summary>Маркировка пакета — наибольший гриф содержимого.</summary>
    public short? Classification { get; set; }

    /// <summary>Причина сбоя — текстом для Администратора (без содержимого материалов).</summary>
    public string? Error { get; set; }

    /// <summary>Файлы носителей отправлены повторно — даже ушедшие прежними пакетами (носитель обмена потерян).</summary>
    public bool ResendFiles { get; set; }
}

/// <summary>
/// Файл носителя, ушедший успешной выгрузкой (<c>investigation.exchange_export_file</c>, ADR-0068): следующий пакет его не
/// повторяет — ссылается на носитель номером источника. Повтор — отметкой «отправить файлы повторно».
/// </summary>
public class ExchangeExportFile
{
    /// <summary>Выгрузка.</summary>
    public int ExportId { get; set; }

    /// <summary>Носитель (номер в пакете «Медиа»).</summary>
    public int AssetId { get; set; }
}
