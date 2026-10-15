using System;
using System.Collections.Generic;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Волна аудиозаписи (ADR-0047): огибающая громкости — наибольший уровень звука в каждом окне записи с равным шагом.
/// Пик <c>i</c> относится к отрезку <c>[i · StepMs, (i + 1) · StepMs)</c>; значение 0–255 — доля полной шкалы
/// 16-битного звука, округлённая ВВЕРХ (любой ненулевой звук виден хотя бы на единицу).
/// </summary>
/// <remarks>
/// Это производная носителя для картинки под проигрывателем, а не обработка записи: звук не меняется (ТЭ-007), волна
/// хранится в строке носителя и уничтожается вместе с ним (ТБ-064). При смене алгоритма построения увеличивается
/// <see cref="CurrentVersion"/> — сохранённые волны прежней версии строятся заново при следующем открытии.
/// </remarks>
/// <param name="StepMs">Шаг между пиками, мс (кратен <see cref="BaseWindowMs"/>).</param>
/// <param name="Peaks">Пики по порядку, 0–255; пустой массив — в записи нет ни одного окна звука.</param>
/// <param name="Version">Версия алгоритма, которым построена волна.</param>
public sealed record AudioWaveform(int StepMs, byte[] Peaks, short Version)
{
    /// <summary>Текущая версия алгоритма построения (см. remarks).</summary>
    public const short CurrentVersion = 1;

    /// <summary>Частота, к которой ffmpeg приводит звук для волны, Гц: огибающей хватает, а декодирование дешёвое.</summary>
    public const int SampleRate = 8000;

    /// <summary>Исходное окно, мс: в нём берётся наибольший уровень звука.</summary>
    public const int BaseWindowMs = 20;

    /// <summary>Наибольшее число пиков: длинная запись укрупняет окна, чтобы волна оставалась небольшой (единицы КБ).</summary>
    public const int MaxPeaks = 3000;

    /// <summary>Длительность, которую покрывает волна, мс (шаг × число пиков; последнее окно может быть неполным).</summary>
    public long DurationMs => (long)StepMs * Peaks.Length;
}

/// <summary>
/// Построение волны из потока звука «моно, 16 бит со знаком, little-endian, <see cref="AudioWaveform.SampleRate"/> Гц»
/// (ADR-0047): поток подаётся кусками любой длины (<see cref="Push"/>), в памяти держится по одному числу на окно
/// <see cref="AudioWaveform.BaseWindowMs"/> мс (часовая запись — 180 000 чисел), затем окна укрупняются до
/// <see cref="AudioWaveform.MaxPeaks"/> (<see cref="Build"/>). Без ffmpeg — чтобы проверять тестами.
/// </summary>
public sealed class AudioWaveformBuilder
{
    /// <summary>Отсчётов в исходном окне: 8000 Гц × 20 мс = 160.</summary>
    public const int SamplesPerWindow = AudioWaveform.SampleRate * AudioWaveform.BaseWindowMs / 1000;

    // Полная шкала 16-битного звука: |−32768| = 32768.
    private const int FullScale = 32768;

    private readonly List<ushort> _windows = [];
    private int _windowPeak;
    private int _windowSamples;
    private int _pendingLowByte = -1;

    /// <summary>Сколько исходных окон накоплено (включая неполное последнее).</summary>
    public int WindowCount => _windows.Count + (_windowSamples > 0 ? 1 : 0);

    /// <summary>
    /// Добавить кусок потока. Куски режутся где угодно, в том числе посреди отсчёта: младший байт оборванного
    /// отсчёта ждёт старшего из следующего куска.
    /// </summary>
    /// <param name="pcm">Байты звука s16le.</param>
    public void Push(ReadOnlySpan<byte> pcm)
    {
        var position = 0;
        if (_pendingLowByte >= 0 && pcm.Length > 0)
        {
            AddSample((short)(_pendingLowByte | (pcm[0] << 8)));
            _pendingLowByte = -1;
            position = 1;
        }

        for (; position + 1 < pcm.Length; position += 2)
        {
            AddSample((short)(pcm[position] | (pcm[position + 1] << 8)));
        }

        if (position < pcm.Length)
        {
            _pendingLowByte = pcm[position];
        }
    }

    /// <summary>Волна по накопленному: неполное последнее окно считается окном; оборванный последний байт отбрасывается.</summary>
    public AudioWaveform Build()
    {
        var windows = new List<ushort>(_windows);
        if (_windowSamples > 0)
        {
            windows.Add((ushort)_windowPeak);
        }

        return Reduce(windows);
    }

    /// <summary>
    /// Укрупнение исходных окон: по <c>k = ⌈n / MaxPeaks⌉</c> подряд в один пик (наибольший), шаг — <c>k × 20</c> мс;
    /// уровень переводится в 0–255 с округлением вверх.
    /// </summary>
    /// <param name="windows">Наибольший |отсчёт| каждого исходного окна, 0–32768.</param>
    public static AudioWaveform Reduce(IReadOnlyList<ushort> windows)
    {
        ArgumentNullException.ThrowIfNull(windows);
        var factor = Math.Max(1, (windows.Count + AudioWaveform.MaxPeaks - 1) / AudioWaveform.MaxPeaks);
        var peaks = new byte[(windows.Count + factor - 1) / factor];
        for (var peak = 0; peak < peaks.Length; peak++)
        {
            var level = 0;
            var end = Math.Min(windows.Count, (peak + 1) * factor);
            for (var window = peak * factor; window < end; window++)
            {
                level = Math.Max(level, windows[window]);
            }

            peaks[peak] = Scale(level);
        }

        return new AudioWaveform(factor * AudioWaveform.BaseWindowMs, peaks, AudioWaveform.CurrentVersion);
    }

    /// <summary>Уровень 0–32768 → 0–255 с округлением вверх: тихий, но не нулевой звук не пропадает с волны.</summary>
    public static byte Scale(int level) =>
        (byte)Math.Clamp((level * 255 + FullScale - 1) / FullScale, 0, 255);

    private void AddSample(short sample)
    {
        var magnitude = Math.Abs((int)sample);
        if (magnitude > _windowPeak)
        {
            _windowPeak = magnitude;
        }

        if (++_windowSamples == SamplesPerWindow)
        {
            _windows.Add((ushort)_windowPeak);
            _windowPeak = 0;
            _windowSamples = 0;
        }
    }
}
