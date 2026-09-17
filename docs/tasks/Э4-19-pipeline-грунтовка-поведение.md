# Э4-19. Сквозное pipeline-поведение грунтовки

| | |
|---|---|
| Этап | Э4 — Первый сквозной срез |
| Статус | ✅ Выполнено (поведение в ядре + маркеры + регистрация + тесты) |
| Требования ТЗ | ТБ-040, ТБ-041; инж-ТЗ §5.3.1.1 (конвейер нельзя обойти), инвариант №1 |
| Документы | [ДОК-05 §2.7](../05_Контракты_и_композиция.md) |
| Зависит от | [Э4-18](Э4-18-профильная-грунтовка-нпа.md), [Э3-10](Э3-10-rag-оркестратор.md) |

## Цель
Сделать грунтовку **необходимой на конвейере**: любой генерирующий сценарий Mediator обязан пройти `IGroundingValidator`; результат с непроверенными ссылками не выдаётся как готовый — помечается. Это `GroundingBehavior` (сквозное pipeline-поведение Mediator), не ручной вызов в хендлере.

## Что сделать
- ✅ Маркеры в `Abstractions`: `IGroundedScenario` (грунтующий запрос Mediator), `IGroundedResult` (результат с вердиктом `AllCitationsConfirmed`), `IPayloadCarrier` (нетипизированный доступ к нагрузке конверта для сквозных поведений).
- ✅ `GroundingBehavior<TMessage,TResponse>` — **в ядре** (`ISC.AI.AI`, где живёт инвариант ТБ-041), ограничение `where TMessage : IGroundedScenario` (применяется только к грунтующим сценариям).
- ✅ Регистрация в хосте после `ValidationBehavior` (порядок конвейера).
- ✅ Помечены `GenerateReferenceCommand : IGroundedScenario`, `GenerateReferenceResult : IGroundedResult`, `ResponseDto<T> : IPayloadCarrier`.
- ✅ Тесты: непроверенные ссылки → пометка на конверте; подтверждённые → чисто; грунтующий сценарий без вердикта → отказ (обход запрещён); неуспех — пропуск.

## Результат
- Поведение [`GroundingBehavior`](../../src/core/ISC.AI.AI/Grounding/GroundingBehavior.cs): грунтующий сценарий **не может вернуть успех без вердикта грунтовки** (иначе конвейер обойдён → fail-closed через ExceptionHandlingBehavior); вывод с непроверенными ссылками помечается на уровне конверта. Живёт в ядре — профиль не ослабляет (ТБ-041).
- Маркеры: [`IGroundedScenario`](../../src/core/ISC.AI.Abstractions/Grounding/IGroundedScenario.cs), [`IGroundedResult`](../../src/core/ISC.AI.Abstractions/Grounding/IGroundedResult.cs), [`IPayloadCarrier`](../../src/core/ISC.AI.Abstractions/Application/IPayloadCarrier.cs).
- Тесты: `tests/ISC.AI.UnitTests/Rag/GroundingBehaviorTests.cs`. Сборка решения 0 ошибок; юнит 84/84.
- Ветка `feature/grounding-behaviors-queue`.

## Примечание по конвейеру
Ограничение `TMessage : IGroundedScenario` фильтрует применение поведения (MS.DI пропускает open-generic с несовместимым constraint — тот же механизм, что у существующего `ExceptionHandlingBehavior` с `TResponse : IResponseDto`). Грунтовку по-прежнему выполняет RAG-оркестратор ядра (единственный путь генерации) — поведение это СТРАХУЕТ, а не дублирует.

## Смежное
- **Audit** как сквозное поведение — отдельная задача [Э4-11](Э4-11-сквозной-аудит-сценариев.md) (P0).
- **Authorization** как поведение — **отложено** вместе с аутентификацией [Э3-08](Э3-08-аутентификация-допуски.md).

## Критерии приёмки
- Генерирующий сценарий нельзя выполнить в обход грунтовки; висячая ссылка → `AllConfirmed=false`, вывод не «готов».
