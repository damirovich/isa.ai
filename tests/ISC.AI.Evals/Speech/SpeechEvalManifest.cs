using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

namespace ISC.AI.Evals.Speech;

/// <summary>Строка манифеста эталонного набора.</summary>
/// <param name="LineNumber">Номер строки в <c>manifest.csv</c> (с единицы) — для сообщений об ошибках.</param>
/// <param name="File">Путь к записи относительно папки набора, как в манифесте.</param>
/// <param name="Language">
/// Язык речи; <see langword="null"/> — контрольный файл без речи (код <see cref="SpeechEvalCodes.NoSpeechCode"/>).
/// </param>
/// <param name="Condition">Условия записи.</param>
/// <param name="Note">Примечание (может быть пустым).</param>
public sealed record SpeechEvalManifestEntry(
    int LineNumber, string File, SpeechLanguage? Language, RecordingCondition Condition, string Note);

/// <summary>Итог разбора манифеста: распознанные строки и ВСЕ найденные ошибки сразу.</summary>
/// <param name="Entries">Строки без ошибок.</param>
/// <param name="Errors">Ошибки с номерами строк.</param>
public sealed record SpeechEvalManifestParseResult(
    IReadOnlyList<SpeechEvalManifestEntry> Entries, IReadOnlyList<string> Errors);

/// <summary>
/// Манифест эталонного набора <c>manifest.csv</c>: <c>файл;язык;условия;примечание</c>, разделитель —
/// точка с запятой (так сохраняет CSV русский Excel), кодировка — UTF-8. Коды языка и условий —
/// <see cref="SpeechEvalCodes"/> (методика пилота, 3.1–3.4).
/// </summary>
/// <remarks>
/// <para>Строка заголовка (первое поле «файл» или «file») пропускается; пустые строки и строки, начинающиеся с
/// <c>#</c>, — тоже. Поля в двойных кавычках допускают <c>;</c> внутри (кавычка внутри — удвоенная, как у
/// Excel). Неэкранированные <c>;</c> в примечании не ошибка: всё после третьего поля — примечание.</para>
/// <para>Ошибки собираются ВСЕ, а не до первой: оператор пилота исправляет манифест за один проход.</para>
/// </remarks>
public static class SpeechEvalManifest
{
    /// <summary>Имя файла манифеста в папке набора.</summary>
    public const string FileName = "manifest.csv";

    /// <summary>Разделитель полей.</summary>
    public const char Separator = ';';

    /// <summary>Разбирает строки манифеста.</summary>
    /// <param name="lines">Строки файла по порядку.</param>
    public static SpeechEvalManifestParseResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var entries = new List<SpeechEvalManifestEntry>();
        var errors = new List<string>();
        var seenFiles = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var lineNumber = 0;
        var firstDataLine = true;

        foreach (var rawLine in lines)
        {
            lineNumber++;
            var line = (lineNumber == 1 ? rawLine.TrimStart('﻿') : rawLine).Trim();
            if (line.Length == 0 || line.StartsWith('#'))
            {
                continue;
            }

            if (!TrySplitFields(line, out var fields))
            {
                errors.Add(Error(lineNumber, "не закрыта двойная кавычка"));
                firstDataLine = false;
                continue;
            }

            if (firstDataLine)
            {
                firstDataLine = false;
                var head = fields[0].Trim();
                if (head.Equals("файл", StringComparison.OrdinalIgnoreCase) || head.Equals("file", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }
            }

            if (fields.Count < 3)
            {
                errors.Add(Error(lineNumber, $"нужно минимум три поля «файл;язык;условия», найдено {fields.Count}"));
                continue;
            }

            var file = fields[0].Trim();
            var lineIsValid = true;
            if (file.Length == 0)
            {
                errors.Add(Error(lineNumber, "не указан файл записи"));
                lineIsValid = false;
            }

            SpeechLanguage? language = null;
            if (SpeechEvalCodes.TryParseLanguage(fields[1], out var parsedLanguage))
            {
                language = parsedLanguage;
            }
            else if (!SpeechEvalCodes.IsNoSpeechCode(fields[1]))
            {
                errors.Add(Error(
                    lineNumber,
                    $"неизвестный язык «{fields[1].Trim()}», допустимо: {SpeechEvalCodes.LanguageCodesHint}{SpeechEvalCodes.LanguageHint(fields[1])}"));
                lineIsValid = false;
            }

            if (!SpeechEvalCodes.TryParseCondition(fields[2], out var condition))
            {
                errors.Add(Error(
                    lineNumber,
                    $"неизвестные условия «{fields[2].Trim()}», допустимо: {SpeechEvalCodes.ConditionCodesHint}{SpeechEvalCodes.ConditionHint(fields[2])}"));
                lineIsValid = false;
            }

            if (file.Length > 0)
            {
                var key = file.Replace('\\', '/');
                if (seenFiles.TryGetValue(key, out var firstLine))
                {
                    errors.Add(Error(lineNumber, $"файл «{file}» уже указан в строке {firstLine.ToString(CultureInfo.InvariantCulture)}"));
                    lineIsValid = false;
                }
                else
                {
                    seenFiles.Add(key, lineNumber);
                }
            }

            if (lineIsValid)
            {
                var note = string.Join(Separator, fields.Skip(3)).Trim();
                entries.Add(new SpeechEvalManifestEntry(lineNumber, file, language, condition, note));
            }
        }

        return new SpeechEvalManifestParseResult(entries, errors);
    }

    /// <summary>
    /// Делит строку на поля по <see cref="Separator"/> с учётом двойных кавычек (как пишет Excel).
    /// Возвращает <see langword="false"/>, если кавычка не закрыта.
    /// </summary>
    /// <param name="line">Строка манифеста.</param>
    /// <param name="fields">Поля без внешних кавычек.</param>
    public static bool TrySplitFields(string line, out IReadOnlyList<string> fields)
    {
        ArgumentNullException.ThrowIfNull(line);

        var result = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        for (var index = 0; index < line.Length; index++)
        {
            var character = line[index];
            if (inQuotes)
            {
                if (character == '"')
                {
                    if (index + 1 < line.Length && line[index + 1] == '"')
                    {
                        current.Append('"');
                        index++;
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(character);
                }
            }
            else if (character == '"' && current.ToString().Trim().Length == 0)
            {
                // Кавычка в начале поля (возможно, после пробелов) открывает поле в кавычках.
                current.Clear();
                inQuotes = true;
            }
            else if (character == Separator)
            {
                result.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(character);
            }
        }

        result.Add(current.ToString());
        fields = result;
        return !inQuotes;
    }

    private static string Error(int lineNumber, string message) =>
        $"{FileName}, строка {lineNumber.ToString(CultureInfo.InvariantCulture)}: {message}";
}
