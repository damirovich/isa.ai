namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Итог индексации носителя (ТФ-МЕД-02): что показать в карточке.</summary>
/// <param name="Success">Конвейер завершён, шаблоны записаны.</param>
/// <param name="Frames">Кадров обработано (видео) или 1 (фото).</param>
/// <param name="Faces">Лиц найдено.</param>
/// <param name="Rejected">Отклонено по качеству (шаблон не строился, ТО-мат-07).</param>
/// <param name="Error">Причина неудачи.</param>
public sealed record MediaIndexResult(bool Success, int Frames, int Faces, int Rejected, string? Error = null);

/// <summary>
/// Исполнитель конвейера индексации носителя (ТП-005, ТО-мат-05): раскадровка → детекция → качество →
/// выравнивание → векторизация → запись шаблонов с грифом носителя. Вызывается фоновой очередью ядра
/// (ТНД-001); повторный запуск идемпотентен (полная перезапись производных носителя).
/// </summary>
public interface IMediaIndexer
{
    /// <summary>Проиндексировать носитель; ошибка фиксируется в статусе носителя и возвращается, не бросается.</summary>
    Task<MediaIndexResult> IndexAsync(int assetId, CancellationToken cancellationToken = default);
}
