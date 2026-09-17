# Э3-05. `IRetriever` с фильтром доступа (fail-closed)

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (контракт, реализация pre-filter, fail-closed тест; GATE-1/timing — Docker/Э3-09) |
| Требования ТЗ | ТБ-020, ТБ-021, ТБ-022, ТБ-023 (инвариант №3) |
| Документы | [ДОК-05 §2.6](../05_Контракты_и_композиция.md), [ДОК-03](../03_Модель_угроз.md) |
| Зависит от | Э3-01, Э3-02 |

## Цель
Реализовать извлечение фрагментов с **обязательным** фильтром доступа по грифу на стороне БД.

## Что сделать
- ✅ Контракт `IRetriever` + `RetrievedChunk` + `RetrievalFilter` в `Abstractions` (`AccessContext` уже был — ADR-0014).
- ✅ Реализация `PgVectorRetriever` в `ISC.AI.AI`: фильтр по грифу/подразделению **на стороне БД до выдачи** (pre-filter, ADR-0007); **fail-closed** без контекста доступа (ТБ-021).
- ⏳ Неразличимость: по контенту — есть (нет доступа → пусто); timing/oracle (текст/время ошибки) — методика GATE-1 на [Э3-09](Э3-09-integrationtests-gate.md). Изоляция грифов (ТБ-023) — pre-filter + денормализация режима на эмбеддинг; раздельные индексы — при необходимости.

## Результат
- Контракты [`IRetriever`](../../src/core/ISC.AI.Abstractions/Retrieval/IRetriever.cs), `RetrievedChunk` (нейтральный), `RetrievalFilter`, [`AccessContextRequiredException`](../../src/core/ISC.AI.Abstractions/Security/AccessContextRequiredException.cs) (fail-closed).
- [`PgVectorRetriever`](../../src/core/ISC.AI.AI/Retrieval/PgVectorRetriever.cs): эмбеддинг запроса (роль Embeddings) → pgvector cosine + `BaselineAccess.Filter` (floor ядра) + `IAccessPolicy` (сужение профиля) + `IsCurrent` — всё **pre-filter на стороне БД** (ТБ-020); topK; проекция в `RetrievedChunk`. Регистрация `AddCoreRetrieval` (+ дефолтная `IAccessPolicy`) в хосте.
- Тесты: unit [`RetrieverFailClosedTests`](../../tests/ISC.AI.UnitTests/Retrieval/RetrieverFailClosedTests.cs) (нет доступа → отказ); интеграционный GATE-1 [`RetrieverAccessFilterTests`](../../tests/ISC.AI.IntegrationTests/Persistence/RetrieverAccessFilterTests.cs) (выше допуска / чужое подразделение / устаревшее не выдаётся; нет доступа = пусто). Сборка 0/0; unit 13/13.

## Осталось
- Прогон GATE-1 на Testcontainers/сервере (Docker) + методика timing/oracle-неразличимости (Э3-09).
- Финальная стратегия pre/post-filter и порог recall — ADR-0007, бенчмарк на Э1.

## Критерии приёмки
- **GATE-1** (см. [Э3-09](Э3-09-integrationtests-gate.md)): материал выше допуска неотличим от «документ отсутствует».
