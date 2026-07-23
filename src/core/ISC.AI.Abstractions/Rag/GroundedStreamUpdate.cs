namespace ISC.AI.Abstractions.Rag;

/// <summary>
/// Обновление потоковой генерации RAG-конвейера (ТО-прог-01). Поток отдаёт СЫРОЙ ЧЕРНОВИК по мере
/// генерации (<see cref="TextDelta"/>), а грунтовка (ТБ-040) выполняется на СОБРАННОМ полном тексте —
/// её вердикт и итог приходят ОДНИМ последним обновлением (<see cref="Final"/>).
/// </summary>
/// <remarks>
/// ИНВАРИАНТ ГРУНТОВКИ (ТБ-040, GATE-2): промежуточные <see cref="TextDelta"/> — это НЕпроверенный
/// черновик; вызывающая сторона обязана показывать их как «идёт проверка», а подтверждённые ссылки и
/// итоговый гриф брать ТОЛЬКО из <see cref="Final"/>. Это согласуется с HITL (результат всегда черновик
/// под проверку человеком, ТБ-042). Ровно одно терминальное обновление с <see cref="Final"/> ≠
/// <see langword="null"/> завершает поток; у него <see cref="TextDelta"/> = <see langword="null"/>.
/// </remarks>
/// <param name="TextDelta">Очередной фрагмент текста черновика; <see langword="null"/> у терминального обновления.</param>
/// <param name="Final">
/// Итог конвейера (ответ + грунтовка + фрагменты + гриф); <see langword="null"/> у всех, кроме последнего.
/// </param>
public sealed record GroundedStreamUpdate(string? TextDelta, GroundedResponse? Final);
