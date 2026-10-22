using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Диктор расшифровки носителя (<c>media.transcript_speaker</c>, ADR-0062): номер автоматической разметки и слой правок
/// сотрудника — имя и объединение с другим диктором, кто и когда правил. Дословный текст фрагментов не меняется.
/// </summary>
/// <remarks>
/// РЕЖИМ. Гриф и подразделение — носителя (ТБ-020): имя диктора может быть ФИО, выборка фильтруется решёткой по самой
/// строке. Строки заменяются вместе с фрагментами при повторной расшифровке и снимаются каскадом с носителем (ТБ-064).
/// </remarks>
public class TranscriptSpeaker : BaseEntity, IClassified
{
    /// <summary>Носитель.</summary>
    public int AssetId { get; set; }

    /// <summary>Носитель (навигация).</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Номер диктора автоматической разметки (с 1).</summary>
    public short Number { get; set; }

    /// <summary>Имя, данное сотрудником («Диктор 1 → объект»); <see langword="null"/> — подпись по номеру.</summary>
    public string? Name { get; set; }

    /// <summary>Слит в диктора с этим номером; <see langword="null"/> — самостоятельный.</summary>
    public short? MergedInto { get; set; }

    /// <summary>Кто последним правил имя или объединение.</summary>
    public int? EditedByUserId { get; set; }

    /// <summary>Когда правили (UTC).</summary>
    public DateTime? EditedAt { get; set; }

    /// <inheritdoc />
    public short Classification { get; set; }

    /// <inheritdoc />
    public int DivisionId { get; set; }
}
