using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;

namespace ISC.AI.Speech.Worker;

/// <summary>
/// Сценарий процесса-распознавателя (ADR-0026): аргументы → проверка файлов → загрузка моделей →
/// распознавание → протокол в stdout, диагностика в stderr, итог — кодом выхода
/// (<see cref="WorkerExitCodes"/>).
/// </summary>
internal static class WorkerApp
{
    /// <summary>Выполняет расшифровку и возвращает код выхода процесса.</summary>
    /// <param name="args">Аргументы командной строки.</param>
    /// <param name="stdout">Поток протокола (только строки протокола — ничего постороннего).</param>
    /// <param name="stderr">Поток диагностики.</param>
    public static int Run(IReadOnlyList<string> args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        WorkerArguments arguments;
        try
        {
            arguments = WorkerArguments.Parse(args);
        }
        catch (WorkerUsageException exception)
        {
            stderr.WriteLine(exception.Message);
            stderr.WriteLine(WorkerArguments.Usage);
            return WorkerExitCodes.InvalidArguments;
        }

        var missing = arguments.FindMissingFiles(File.Exists);
        if (missing.Count > 0)
        {
            foreach (var line in missing)
            {
                stderr.WriteLine(line);
            }

            return WorkerExitCodes.InvalidArguments;
        }

        return Transcribe(arguments, stdout, stderr);
    }

    // Отдельный метод без встраивания: всё, что касается sherpa-onnx, собрано здесь, и разбор аргументов
    // выше не тянет загрузку нативной сборки (тесты проверяют его без неё).
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Transcribe(WorkerArguments arguments, TextWriter stdout, TextWriter stderr)
    {
        Pcm16WaveReader reader;
        try
        {
            reader = Pcm16WaveReader.Open(new FileStream(
                arguments.InputPath, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, FileOptions.SequentialScan));
        }
        catch (Exception exception) when (exception is InvalidDataException or IOException or UnauthorizedAccessException)
        {
            stderr.WriteLine($"Вход не прочитан ({Path.GetFullPath(arguments.InputPath)}): {exception.Message}");
            return WorkerExitCodes.InvalidArguments;
        }

        using (reader)
        {
            // Заведомо слишком длинная запись (32-битные индексы нативного детектора, ADR-0026) отвергается ДО загрузки
            // моделей: иначе она упала бы лишь после многочасового прогона. В режиме --decode-whole детектора нет.
            if (!arguments.DecodeWhole && reader.ExpectedSamples is { } expected && VadSettings.ExceedsIndexLimit(expected))
            {
                stderr.WriteLine(VadSettings.TooLongMessage(expected));
                return WorkerExitCodes.InvalidArguments;
            }

            SherpaSpeechRecognizer recognizer;
            var loading = Stopwatch.StartNew();
            try
            {
                recognizer = SherpaSpeechRecognizer.Load(arguments);
            }
            catch (ModelLoadException exception)
            {
                stderr.WriteLine(exception.Message);
                return WorkerExitCodes.ModelLoadFailed;
            }

            var mode = arguments.DecodeWhole ? $"; ДИАГНОСТИКА {WorkerArguments.DecodeWholeKey}: вся запись без детектора речи." : ".";
            stderr.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Модели загружены за {loading.Elapsed.TotalSeconds:0.0} с; потоков {arguments.Threads}, кусок до {arguments.MaxSegmentSeconds} с{mode}"));

            using (recognizer)
            {
                var writer = new ProtocolWriter(stdout);
                var running = Stopwatch.StartNew();
                try
                {
                    var durationMs = arguments.DecodeWhole ? recognizer.RunWhole(reader, writer) : recognizer.Run(reader, writer);
                    writer.WriteDone(durationMs);
                    stderr.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"Готово: {writer.SegmentsWritten} фрагм. (кусков декодировано {recognizer.PiecesDecoded}), запись {durationMs / 1000.0:0.0} с, распознавание {running.Elapsed.TotalSeconds:0.0} с."));
                    return WorkerExitCodes.Success;
                }
                catch (InputTooLongException exception)
                {
                    // Вход не годится — код 2, как у неверного WAV. Строки done нет: уже записанные фрагменты
                    // адаптер как законченную расшифровку не сохранит.
                    stderr.WriteLine($"{exception.Message} Прервано после {writer.SegmentsWritten} фрагм.");
                    return WorkerExitCodes.InvalidArguments;
                }
                catch (Exception exception)
                {
                    // Любая ошибка посреди прогона — код 4 и причина в stderr. Строки done при этом НЕТ:
                    // адаптер по её отсутствию понимает, что фрагменты неполные, и не сохранит обрывок
                    // как законченную расшифровку.
                    stderr.WriteLine($"Ошибка распознавания после {writer.SegmentsWritten} фрагм.: {exception.GetType().Name}: {exception.Message}");
                    return WorkerExitCodes.RecognitionFailed;
                }
            }
        }
    }
}
