using ISC.AI.Abstractions.Security;

namespace ISC.AI.Abstractions.Rag;

/// <summary>
/// RAG-оркестратор (ТО-мат-01): связывает извлечение, генерацию и грунтовку в единый конвейер.
/// </summary>
/// <remarks>
/// Порядок (И-AI-2, ТБ-020/040/041): извлечение с ОБЯЗАТЕЛЬНЫМ фильтром доступа (модель не получает
/// фрагменты выше допуска) → сборка промпта «системный (ядро, грунтовка) + задачный (профиль) +
/// фрагменты» → генерация → ГРУНТОВКА вывода против извлечённых фрагментов → результат с перечнем
/// использованных фрагментов (для аудита и наследования грифа, ТБ-032/033). Конвейер нельзя обойти:
/// любой генерирующий сценарий проходит через него (сквозное pipeline-поведение — на стороне хоста/Mediator).
/// </remarks>
public interface IGroundedGenerator
{
    /// <summary>Выполняет RAG-конвейер для запроса в контексте доступа субъекта (блокирующе, ответ целиком).</summary>
    Task<GroundedResponse> GenerateAsync(
        GroundedRequest request,
        AccessContext access,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Потоковый вариант конвейера (ТО-прог-01): отдаёт черновик по мере генерации
    /// (<see cref="GroundedStreamUpdate.TextDelta"/>), затем ОДНИМ последним обновлением — итог с грунтовкой
    /// (<see cref="GroundedStreamUpdate.Final"/>). Инварианты те же: фильтр доступа ДО модели (ТБ-020),
    /// грунтовка на СОБРАННОМ полном тексте (ТБ-040), аудит генерации (ТБ-030). Промежуточный текст —
    /// НЕпроверенный черновик (см. <see cref="GroundedStreamUpdate"/>).
    /// </summary>
    IAsyncEnumerable<GroundedStreamUpdate> GenerateStreamingAsync(
        GroundedRequest request,
        AccessContext access,
        CancellationToken cancellationToken = default);
}
