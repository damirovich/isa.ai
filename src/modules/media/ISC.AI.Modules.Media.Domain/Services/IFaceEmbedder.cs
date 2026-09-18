using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>
/// Порт векторизации лица (ТО-мат-05): выровненное по ключевым точкам лицо → L2-нормированный
/// шаблон фиксированной размерности. Косинусная схожесть двух шаблонов = скалярное произведение.
/// </summary>
/// <remarks>
/// Шаблон — биометрические данные (ТБ-070): порт возвращает вектор вызывающему, который обязан
/// сохранить его только с грифом/подразделением носителя. Баллы схожести привязаны к
/// <see cref="ModelVersion"/>: смена модели — переиндексация (ТО-мат-09).
/// </remarks>
public interface IFaceEmbedder
{
    /// <summary>Размерность шаблона (константа модели; у SFace — 128).</summary>
    int Dimensions { get; }

    /// <summary>Версия/идентификатор модели векторизатора.</summary>
    string ModelVersion { get; }

    /// <summary>Вычисляет шаблон лица <paramref name="face"/> на изображении <paramref name="imageBytes"/>.</summary>
    Task<float[]> EmbedAsync(byte[] imageBytes, DetectedFace face, CancellationToken cancellationToken = default);
}
