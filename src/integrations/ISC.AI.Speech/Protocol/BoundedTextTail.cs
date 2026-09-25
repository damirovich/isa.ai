using System.Text;

namespace ISC.AI.Speech.Protocol;

/// <summary>
/// Хвост stderr процесса-распознавателя ограниченного размера — для текста ошибки. Многочасовой прогон
/// не должен копить весь поток диагностики в памяти хоста; для объяснения сбоя достаточно последних строк.
/// Потокобезопасен: строки приходят из потока чтения stderr, читаются — из потока вызова.
/// </summary>
internal sealed class BoundedTextTail(int maxChars)
{
    private readonly object _gate = new();
    private readonly StringBuilder _text = new();

    /// <summary>Добавляет строку; при переполнении отбрасываются самые старые символы.</summary>
    public void AppendLine(string line)
    {
        lock (_gate)
        {
            _text.Append(line).Append('\n');
            if (_text.Length > maxChars)
            {
                _text.Remove(0, _text.Length - maxChars);
            }
        }
    }

    /// <summary>Текущее содержимое без концевых переводов строк.</summary>
    public override string ToString()
    {
        lock (_gate)
        {
            return _text.ToString().TrimEnd();
        }
    }
}
