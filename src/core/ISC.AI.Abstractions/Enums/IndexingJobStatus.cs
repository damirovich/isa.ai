namespace ISC.AI.Abstractions.Enums;

/// <summary>Статус задания индексации.</summary>
public enum IndexingJobStatus
{
    /// <summary>Ожидает обработки.</summary>
    Pending = 0,

    /// <summary>Выполняется.</summary>
    Running = 1,

    /// <summary>Завершено успешно.</summary>
    Completed = 2,

    /// <summary>Завершено с ошибкой.</summary>
    Failed = 3,
}
