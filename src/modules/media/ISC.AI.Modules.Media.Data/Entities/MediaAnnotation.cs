using ISC.AI.Abstractions.Entities;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Data.Entities;

/// <summary>
/// Пометка на фотографии (<c>media.annotation</c>, ТФ-МЕД-13, ADR-0050): стрелка, рамка или подпись поверх
/// изображения. Слой отдельно от оригинала (ТЭ-007): файл не меняется, пометки рисуются поверх и наносятся только в
/// выгружаемую копию по выбору.
/// </summary>
/// <remarks>
/// РЕЖИМ. Гриф и подразделение ДЕНОРМАЛИЗОВАНЫ с носителя (ТБ-020/070), как у лиц и фрагментов расшифровки: выборка
/// фильтруется решёткой по самой строке на стороне БД. Физически удаляемая строка: снимается каскадом FK вместе с
/// носителем — уничтожение носителя или дела уносит и пометки (ТНД-008). Автор и время — в строке и в журнале (ТБ-030).
/// </remarks>
public class MediaAnnotation : BaseEntity, IClassified
{
    /// <summary>Носитель (фото).</summary>
    public int AssetId { get; set; }

    /// <summary>Навигация к носителю.</summary>
    public MediaAsset? Asset { get; set; }

    /// <summary>Вид пометки.</summary>
    public AnnotationKind Kind { get; set; }

    /// <summary>Первая точка, X (пиксели изображения после поворота по EXIF).</summary>
    public double X1 { get; set; }

    /// <summary>Первая точка, Y.</summary>
    public double Y1 { get; set; }

    /// <summary>Вторая точка, X.</summary>
    public double X2 { get; set; }

    /// <summary>Вторая точка, Y.</summary>
    public double Y2 { get; set; }

    /// <summary>Текст подписи.</summary>
    public string? Text { get; set; }

    /// <summary>Цвет из палитры (<see cref="AnnotationColors"/>).</summary>
    public required string Color { get; set; }

    /// <summary>Кто поставил пометку.</summary>
    public int? AuthorUserId { get; set; }

    /// <summary>Гриф (денормализован с носителя, ТБ-020). NOT NULL.</summary>
    public short Classification { get; set; }

    /// <summary>Подразделение (денормализовано с носителя, ТБ-020). NOT NULL.</summary>
    public int DivisionId { get; set; }
}
