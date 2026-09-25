using System.Globalization;

namespace ISC.AI.Modules.Media.UI;

/// <summary>
/// Разбор числовых параметров адресной строки страниц пакета «Медиа» (<c>?t=</c>, <c>?face=</c>,
/// <c>?caseId=</c>, <c>?faceId=</c>, <c>?person=</c>).
/// </summary>
/// <remarks>
/// Страницы принимают такие параметры СТРОКОЙ (<c>[SupplyParameterFromQuery] string?</c>), а не <c>int?</c>/<c>long?</c>:
/// нечисловое или переполненное значение (ссылку правили вручную, обрезали при пересылке) Blazor сам не
/// разобрал бы и уронил страницу — ошибка 500 на предрендере, разрыв circuit'а при интерактивном переходе.
/// Здесь неразобранное значение — просто «параметра нет». Принимаются только десятичные цифры без знака,
/// пробелов и разделителей (<see cref="NumberStyles.None"/>, инвариантная культура): ссылки пакета строятся
/// из неотрицательных идентификаторов и миллисекунд, другого вида у корректного значения не бывает.
/// </remarks>
public static class MediaQueryValues
{
    /// <summary>Целое из параметра адреса; <see langword="null"/> — параметра нет или он не разбирается.</summary>
    /// <param name="value">Значение параметра как пришло в адресе.</param>
    public static int? ParseInt(string? value) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;

    /// <summary>Длинное целое из параметра адреса; <see langword="null"/> — параметра нет или он не разбирается.</summary>
    /// <param name="value">Значение параметра как пришло в адресе.</param>
    public static long? ParseLong(string? value) =>
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number : null;
}
