// Точка входа процесса-распознавателя речи (ADR-0026). Запускается адаптером ISC.AI.Speech:
//   ISC.AI.Speech.Worker --model <model.onnx> --tokens <tokens.txt> --vad <silero_vad.onnx> --input <wav>
//                        [--threads N] [--max-segment-seconds 20] [--feature-dim 64] [--decode-whole]
// --decode-whole — ДИАГНОСТИКА, адаптер его не передаёт: вся запись без детектора речи подаётся модели кусками
// (проверка пути декодирования на синтетическом звуке без записей людей; тишину и тон детектор до модели не пускает).
// stdout — ТОЛЬКО строки протокола (JSON, UTF-8), stderr — диагностика, код выхода — WorkerExitCodes.
// Потоки открываются напрямую с UTF-8 без BOM: кодовая страница консоли Windows (866/1251) исказила бы
// кириллицу, а BOM в начале stdout сломал бы разбор первой строки.
using System.Text;
using ISC.AI.Speech.Worker;

var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
using var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8) { AutoFlush = false, NewLine = "\n" };
using var stderr = new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true, NewLine = "\n" };

// Нативный sherpa-onnx пишет свои сообщения в stderr процесса мимо этого писателя — это нормально:
// адаптер собирает stderr целиком.
return WorkerApp.Run(args, stdout, stderr);
