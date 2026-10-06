using ISC.AI.Modules.Media.Domain.Model;
using ISC.AI.Modules.Media.Domain.Services;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.UI;

/// <summary>Выбор в окне эталона: носитель и лицо на нём.</summary>
/// <param name="AssetId">Носитель (фото или видео дела).</param>
/// <param name="FaceId">Лицо на носителе.</param>
public sealed record ReferencePick(int AssetId, int FaceId);

/// <summary>
/// Правила выбора эталона мышкой (ТБ-077, окно «Новый эталон» карточки фигуранта): какие носители дела годятся
/// в источник и какое лицо можно выбрать. Вынесены из разметки, чтобы проверяться модульными тестами.
/// </summary>
/// <remarks>
/// Окончательную проверку делает сервер (<c>AddReferencePhotoCommand</c>): носитель — из дела фигуранта и в допуске,
/// фото или видео, лицо — на этом носителе. Здесь — только подсказка интерфейса, не граница безопасности.
/// </remarks>
public static class ReferencePickerRules
{
    /// <summary>Расширения, которые окно принимает для загрузки нового эталона (фото; TIFF/HEIC конвейер не читает, ADR-0020).</summary>
    public const string UploadAccept = ".jpg,.jpeg,.png,.bmp,.webp";

    /// <summary>Предел размера загружаемого эталона: фото 3×4 или скан — единицы мегабайт; предпросмотр держится в памяти.</summary>
    public const long MaxUploadBytes = 20L * 1024 * 1024;

    /// <summary>
    /// Носители дела, из которых можно взять эталон: фото и видео (аудиозапись лица не несёт, ADR-0026); фото —
    /// первыми, внутри — новые первыми.
    /// </summary>
    public static IReadOnlyList<MediaAssetRow> SourceAssets(IEnumerable<MediaAssetRow> assets) =>
        assets.Where(a => a.Kind is MediaKind.Image or MediaKind.Video)
            .OrderBy(a => a.Kind == MediaKind.Image ? 0 : 1)
            .ThenByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToList();

    /// <summary>
    /// Лицо можно выбрать эталоном, только если по нему строится шаблон: эталон — образец для поиска по лицу
    /// (ТФ-ПЛ-03), а непригодное лицо (ТО-мат-07) образцом быть не может.
    /// </summary>
    public static bool CanPick(FaceRow face) => face.QualityAcceptable;

    /// <summary>
    /// Лицо, выбираемое автоматически: единственное пригодное на носителе (фото 3×4, скан паспорта). Если пригодных
    /// несколько или нет — <see langword="null"/>, выбирает человек.
    /// </summary>
    public static FaceRow? AutoPick(IReadOnlyList<FaceRow> faces) =>
        faces.Count(CanPick) == 1 ? faces.Single(CanPick) : null;

    /// <summary>
    /// Фото анкеты фигуранта (ТФ-ПЕР-05): актуальный (не заменённый, ТБ-077) эталон с выбранным лицом, последний
    /// добавленный. Эталон без лица фото анкеты не служит — показать нечего; нет такого — <see langword="null"/>.
    /// </summary>
    public static ReferencePhotoRow? QuestionnairePhoto(IEnumerable<ReferencePhotoRow> photos) =>
        photos.Where(p => p.SupersededById is null && p.MediaFaceId is not null).MaxBy(p => p.Id);
}
