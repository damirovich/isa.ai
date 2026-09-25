namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Итог восстановления при старте хоста: сколько носителей переведено из «в очереди / выполняется» в «ошибка»,
/// потому что их задачи пропали вместе с очередью в памяти прежнего процесса.
/// </summary>
/// <param name="Transcriptions">Расшифровки речи (статусы «в очереди» и «идёт расшифровка»).</param>
/// <param name="Indexings">Индексации лиц (статус «обрабатывается»).</param>
public sealed record InterruptedWorkRecovery(int Transcriptions, int Indexings)
{
    /// <summary>Ничего не было прервано.</summary>
    public bool IsEmpty => Transcriptions == 0 && Indexings == 0;
}
