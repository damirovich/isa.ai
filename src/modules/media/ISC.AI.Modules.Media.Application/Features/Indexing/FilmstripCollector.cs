using System;
using System.Collections.Generic;

namespace ISC.AI.Modules.Media.Application.Features.Indexing;

/// <summary>Кадр ленты: момент записи, мс, и уменьшенная картинка (JPEG).</summary>
/// <param name="TimestampMs">Момент кадра от начала записи, мс.</param>
/// <param name="Jpeg">Плитка ленты.</param>
public sealed record FilmstripTile(long TimestampMs, byte[] Jpeg);

/// <summary>
/// Отбор кадров раскадровки в ленту видео (ADR-0038) БЕЗ лишнего прохода по файлу: кадры уже идут через конвейер
/// индексации (поиск лиц), лента берёт часть из них. Длительность заранее может быть неизвестна (проба не удалась,
/// незавершённый Matroska), поэтому отбор — прореживанием: берётся каждый кадр с шагом <c>stride</c>; набралось больше
/// <see cref="MaxTiles"/> — остаётся каждый второй, а шаг удваивается. Плитки всегда идут с РАВНЫМ шагом от начала
/// записи, их число — от половины до <see cref="MaxTiles"/> (у коротких записей — сколько кадров есть).
/// </summary>
/// <remarks>
/// Уменьшать кадр стоит только для отобранных: <see cref="Offer"/> вызывается на КАЖДЫЙ кадр выборки по порядку и
/// говорит, нужен ли он; <see cref="Add"/> — уже с плиткой. Кадр, который не удалось уменьшить, просто пропускается:
/// лента — подсказка для навигации, а не материал дела.
/// </remarks>
public sealed class FilmstripCollector
{
    /// <summary>Предел кадров в ленте по умолчанию: на любой ширине экрана плиток видно меньше.</summary>
    public const int DefaultMaxTiles = 120;

    private readonly List<FilmstripTile> _tiles = [];
    private readonly List<int> _ordinals = [];
    private int _stride = 1;
    private int _seen;

    /// <summary>Сборщик ленты не более чем из <paramref name="maxTiles"/> кадров.</summary>
    public FilmstripCollector(int maxTiles = DefaultMaxTiles)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxTiles, 2);
        MaxTiles = maxTiles;
    }

    /// <summary>Предел кадров в ленте.</summary>
    public int MaxTiles { get; }

    /// <summary>Отобранные кадры по времени.</summary>
    public IReadOnlyList<FilmstripTile> Tiles => _tiles;

    /// <summary>
    /// Шаг между соседними кадрами ленты, мс (среднее по отобранным); один кадр — 0. Плитка <c>i</c> — момент
    /// ≈ <c>i · StepMs</c>: раскадровка начинается с нуля и идёт с постоянной частотой.
    /// </summary>
    public long StepMs => _tiles.Count < 2
        ? 0
        : (long)Math.Round((double)(_tiles[^1].TimestampMs - _tiles[0].TimestampMs) / (_tiles.Count - 1));

    /// <summary>Очередной кадр выборки (по порядку): нужен ли он ленте.</summary>
    public bool Offer()
    {
        var take = _seen % _stride == 0;
        _seen++;
        return take;
    }

    /// <summary>
    /// Добавить кадр, только что одобренный <see cref="Offer"/>; при переполнении лента прореживается вдвое.
    /// </summary>
    public void Add(long timestampMs, byte[] jpeg)
    {
        ArgumentNullException.ThrowIfNull(jpeg);
        _tiles.Add(new FilmstripTile(timestampMs, jpeg));
        _ordinals.Add(_seen - 1);
        while (_tiles.Count > MaxTiles)
        {
            Thin();
        }
    }

    // Остаются кадры с номером выборки, кратным удвоенному шагу (0, 2s, 4s…): равный шаг от начала записи сохраняется,
    // даже если какой-то кадр не удалось уменьшить и его в ленте нет.
    private void Thin()
    {
        var stride = _stride * 2;
        for (var i = _tiles.Count - 1; i >= 0; i--)
        {
            if (_ordinals[i] % stride != 0)
            {
                _tiles.RemoveAt(i);
                _ordinals.RemoveAt(i);
            }
        }

        _stride = stride;
    }
}
