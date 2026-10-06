namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Лента кадров видео, собранная при индексации (ADR-0038): одна картинка JPEG — уменьшенные кадры раскадровки в ряд
/// с равным шагом по времени. Показывается под проигрывателем; ничего не «улучшает» (ТЭ-007) — только масштаб.
/// </summary>
/// <param name="StoredFileName">Имя картинки в хранилище (категория <c>media-filmstrips</c>, подкаталог — носитель).</param>
/// <param name="TileCount">Сколько кадров в ряду (плиток одинаковой ширины).</param>
/// <param name="StepMs">Шаг между кадрами ленты, мс: плитка <c>i</c> — кадр записи в момент ≈ <c>i · StepMs</c>.</param>
public sealed record FilmstripDraft(string StoredFileName, int TileCount, long StepMs);
