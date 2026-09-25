using System;
using System.Collections.Generic;
using System.Text;

namespace ISC.AI.Speech.Worker;

/// <summary>Кусок участка речи, ожидающий склейки: границы на шкале записи и его отсчёты.</summary>
/// <param name="Start">Начало, отсчёты от начала записи.</param>
/// <param name="End">Конец (не включая), отсчёты от начала записи.</param>
/// <param name="Samples">Отсчёты куска (<c>End − Start</c> штук).</param>
internal sealed record PendingPiece(long Start, long End, float[] Samples);

/// <summary>Слова, отнесённые к участку склейки: текст и где на шкале записи они прозвучали.</summary>
/// <param name="Text">Слова участка через пробел; пустая строка — слов нет.</param>
/// <param name="WordsStart">Метка первого токена первого слова, отсчёты от начала записи (без слов — 0).</param>
/// <param name="WordsEnd">Метка последнего токена последнего слова плюс шаг метки, отсчёты (без слов — 0).</param>
internal readonly record struct PieceWords(string Text, long WordsStart, long WordsEnd)
{
    /// <summary>Слов нет — фрагмент в протокол не пишется.</summary>
    public bool IsEmpty => Text.Length == 0;
}

/// <summary>
/// Склейка соседних участков речи в один кусок для модели и раскладка распознанных слов обратно по участкам
/// (ADR-0026). Здесь нет типов sherpa-onnx — всё проверяется юнит-тестами без нативной библиотеки и моделей.
/// </summary>
/// <remarks>
/// <para>ЗАЧЕМ СКЛЕИВАТЬ. GigaAM Multilingual знает и киргизский, и близкий к нему казахский и выбирает язык сама,
/// по звуку. На коротком участке детектора (1–3 с: «бир аз кечигип калдым») звука для выбора мало, и модель
/// иногда пишет киргизскую речь казахскими словами и буквами («біраз кешігіп қалдым») — для поиска по
/// расшифровке это потерянные слова. Замер на живой киргизской записи (25.09.2026, 30 с): на участках детектора —
/// 2 «казахских» фрагмента из 8 у обеих моделей, на кусках ~15 с по тем же паузам — ни одного; WER 600M
/// 23,6 % → 10,9 %. Поэтому соседние участки подаются модели одним куском не длиннее предела модели, вместе со
/// звуком пауз между ними (на шкале записи кусок сплошной).</para>
/// <para>ТАЙМКОДЫ НЕ ГРУБЕЮТ. Модель отдаёт метку времени на каждый токен (у GigaAM — буква, шаг 40 мс; слова
/// разделены токеном-пробелом). Слово относится к участку, в который попадает его первый токен; слово в паузе
/// между участками — к ближайшему. Фрагменты в протоколе остаются по участкам детектора, как без склейки.</para>
/// <para>СЛОВО В ПАУЗЕ НЕ УВОДИТ ТАЙМКОД. Модель слышит и паузы между участками и может найти в них слово, которое
/// детектор не принял за речь (короткое тихое «да» короче <see cref="VadSettings.MinSpeechSeconds"/>). Такое слово
/// попадает в текст ближайшего участка, и границы его фрагмента расширяются до места слова
/// (<see cref="FragmentBounds"/>): переход к фрагменту из поиска не должен перематывать запись уже ПОСЛЕ искомого
/// слова. Соседние фрагменты при этом не перекрываются.</para>
/// </remarks>
internal static class SpeechChunks
{
    /// <summary>
    /// Шаг метки времени токена — 40 мс (кадр GigaAM после прореживания признаков): конец слова — метка его
    /// последнего токена плюс шаг.
    /// </summary>
    public const int TokenStepSamples = VadSettings.SampleRate / 25;
    /// <summary>
    /// Запас после предела куска, по истечении которого набранный кусок подаётся модели: детектор отдаёт участок
    /// только после паузы <see cref="VadSettings.MinSilenceSeconds"/> за его концом (плюс окна на спад вероятности
    /// речи), поэтому участок, ещё не отданный детектором, кончается не раньше «прочитано − запас».
    /// </summary>
    public const int SealMarginSamples = VadSettings.MinSilenceSamples + 3 * VadSettings.WindowSize;

    /// <summary>
    /// Помещается ли кусок в набираемый: весь склеенный кусок — от начала первого участка до конца нового,
    /// вместе с паузами — не длиннее предела модели.
    /// </summary>
    /// <param name="chunkStart">Начало набираемого куска, отсчёты.</param>
    /// <param name="pieceEnd">Конец присоединяемого куска, отсчёты.</param>
    /// <param name="maxChunkSamples">Предел куска для модели, отсчёты.</param>
    public static bool Fits(long chunkStart, long pieceEnd, int maxChunkSamples) => pieceEnd - chunkStart <= maxChunkSamples;

    /// <summary>
    /// Набранный кусок пора подать модели: ни один участок, который детектор ещё отдаст, в предел уже не
    /// уложится. Так кусок не ждёт дольше предела плюс запас, и его звук ещё есть в истории утилиты.
    /// </summary>
    /// <param name="chunkStart">Начало набираемого куска, отсчёты.</param>
    /// <param name="samplesRead">Сколько отсчётов записи уже прочитано.</param>
    /// <param name="maxChunkSamples">Предел куска для модели, отсчёты.</param>
    public static bool IsSealed(long chunkStart, long samplesRead, int maxChunkSamples) =>
        samplesRead - SealMarginSamples > chunkStart + maxChunkSamples;

    /// <summary>
    /// Раскладывает распознанный текст куска по его участкам по меткам времени токенов.
    /// </summary>
    /// <param name="tokens">Токены результата модели (у GigaAM — буквы и пробел; у моделей SentencePiece начало
    /// слова помечено «▁»).</param>
    /// <param name="timestamps">Метка времени каждого токена, секунды от начала куска.</param>
    /// <param name="chunkStart">Начало куска на шкале записи, отсчёты.</param>
    /// <param name="pieces">Участки куска по порядку, на шкале записи: [Start, End).</param>
    /// <param name="sampleRate">Частота дискретизации, Гц.</param>
    /// <returns>Слова каждого участка (<see cref="PieceWords.IsEmpty"/> — слов нет); <see langword="null"/> — меток
    /// нет или их число не совпадает с числом токенов (тогда вызывающий пишет один фрагмент на весь кусок).</returns>
    public static PieceWords[]? AssignWords(
        IReadOnlyList<string>? tokens, IReadOnlyList<float>? timestamps, long chunkStart,
        IReadOnlyList<(long Start, long End)> pieces, int sampleRate)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        if (pieces.Count == 0 || tokens is null || timestamps is null || tokens.Count != timestamps.Count)
        {
            return null;
        }

        var words = new List<StringBuilder>[pieces.Count];
        var firstPosition = new long?[pieces.Count];
        var lastPosition = new long[pieces.Count];
        for (var i = 0; i < words.Length; i++)
        {
            words[i] = [];
        }

        StringBuilder? current = null;
        var currentPiece = 0;
        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i] ?? string.Empty;
            var startsWord = token.Length > 0 && (token[0] == ' ' || token[0] == '▁');
            var body = token.Trim(' ', '▁');
            if (startsWord || body.Length == 0)
            {
                current = null; // разделитель слов (или начало нового слова у моделей SentencePiece)
            }

            if (body.Length == 0)
            {
                continue;
            }

            var position = chunkStart + (long)Math.Round(timestamps[i] * sampleRate);
            if (current is null)
            {
                currentPiece = PieceIndex(pieces, position);
                current = new StringBuilder();
                words[currentPiece].Add(current);
                firstPosition[currentPiece] ??= position;
            }

            current.Append(body);
            lastPosition[currentPiece] = Math.Max(lastPosition[currentPiece], position + TokenStepSamples);
        }

        var result = new PieceWords[pieces.Count];
        for (var i = 0; i < result.Length; i++)
        {
            result[i] = words[i].Count == 0
                ? new PieceWords(string.Empty, 0, 0)
                : new PieceWords(string.Join(' ', words[i]), firstPosition[i]!.Value, lastPosition[i]);
        }

        return result;
    }

    /// <summary>
    /// Границы фрагментов склейки: участок, расширенный до места его слов (слово, найденное моделью в паузе, не
    /// должно оказаться вне таймкода своего фрагмента), в пределах склейки и без перекрытия с предыдущим
    /// фрагментом. У участка без слов — его собственные границы (такой фрагмент не пишется).
    /// </summary>
    /// <param name="pieces">Участки склейки по порядку, на шкале записи.</param>
    /// <param name="words">Результат <see cref="AssignWords"/> для этих участков.</param>
    /// <param name="chunkStart">Начало склейки, отсчёты.</param>
    /// <param name="chunkEnd">Конец склейки, отсчёты.</param>
    public static (long Start, long End)[] FragmentBounds(
        IReadOnlyList<(long Start, long End)> pieces, IReadOnlyList<PieceWords> words, long chunkStart, long chunkEnd)
    {
        ArgumentNullException.ThrowIfNull(pieces);
        ArgumentNullException.ThrowIfNull(words);

        var bounds = new (long Start, long End)[pieces.Count];
        var previousEnd = chunkStart;
        for (var i = 0; i < pieces.Count; i++)
        {
            if (words[i].IsEmpty)
            {
                bounds[i] = pieces[i];
                continue;
            }

            var start = Math.Clamp(Math.Min(pieces[i].Start, words[i].WordsStart), chunkStart, chunkEnd);
            var end = Math.Clamp(Math.Max(pieces[i].End, words[i].WordsEnd), chunkStart, chunkEnd);
            start = Math.Max(start, previousEnd);
            end = Math.Max(end, start);
            bounds[i] = (start, end);
            previousEnd = end;
        }

        return bounds;
    }

    /// <summary>Текст без лишних пробелов — для сверки раскладки с текстом модели.</summary>
    public static string NormalizeSpaces(string? text) =>
        string.Join(' ', (text ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    // Участок, которому принадлежит отсчёт: содержащий его, а в паузе между участками — ближайший (при равенстве —
    // следующий: метка CTC обычно чуть запаздывает, и слово в паузе скорее начинает следующую фразу). Позиции токенов
    // не убывают, поэтому и номера участков у слов не убывают — порядок слов сохраняется.
    private static int PieceIndex(IReadOnlyList<(long Start, long End)> pieces, long position)
    {
        for (var k = 0; k < pieces.Count; k++)
        {
            if (position < pieces[k].End)
            {
                if (position >= pieces[k].Start || k == 0)
                {
                    return k;
                }

                return position - pieces[k - 1].End < pieces[k].Start - position ? k - 1 : k;
            }
        }

        return pieces.Count - 1;
    }
}
