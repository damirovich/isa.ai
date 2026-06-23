# Э3-02. Интеграция pgvector

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (код, миграция; векторный поиск на Testcontainers — при наличии Docker) |
| Требования ТЗ | ТО-инф-02, ТБ-022 |
| Документы | [ДОК-04 §8](../04_Схема_данных.md) |
| Зависит от | Э3-01 |

## Цель
Подключить векторное хранилище pgvector в той же БД и индекс для семантического поиска.

## Что сделать
- ✅ `CREATE EXTENSION vector` — миграция `AddCoreEmbeddings` (`HasPostgresExtension("vector")`).
- ✅ Колонка `vector(N)` (`EmbeddingEntity`, N=768 под EmbeddingGemma, ADR-0011) + ANN-индекс HNSW (косинус).
- ⏳ Стратегия фильтрации векторного поиска по грифу (pre/post-filter) — обосновать в ADR-0007 (ТБ-022, калибровка на Э1).

## Результат
- [`EmbeddingEntity`](../../src/core/ISC.AI.Persistence/Entities/EmbeddingEntity.cs) (схема `core`, аудируемый/физически удаляемый ТБ-064): `ChunkId` (FK→chunk, cascade), `Embedding` `vector(768)`, `ModelKey`; режимные `Classification`/`DivisionId` и флаг `IsCurrent` **денормализованы на вектор** — фильтр доступа (ТБ-020) и фильтр актуальности (ADR-0013) применяются на стороне БД без джойнов.
- [Конфигурация](../../src/core/ISC.AI.Persistence/EntityConfigurations/CorpusConfigurations/EmbeddingEntityConfiguration.cs): ANN-индекс `ixEmbeddingVectorHnsw` (HNSW, `vector_cosine_ops`) + B-tree `ixEmbeddingClassificationDivision` под pre-filter; `DbSet<EmbeddingEntity>` в `CoreDbContext`; `HasPostgresExtension("vector")`; `UseVector()` в `AddCorePersistence` и design-фабрике.
- Миграция `AddCoreEmbeddings` (расширение `vector` + таблица `core.embedding` + индексы). Сборка 0/0; модель совпадает с миграцией (`has-pending-model-changes` — нет изменений).

## Осталось
- Прогон векторного поиска на Testcontainers (Postgres+pgvector) с фильтром по грифу на стороне БД — **при наличии Docker** (критерий приёмки ниже).
- Финальный выбор pre/post-filter — ADR-0007 (ТБ-022), Э1.

## Критерии приёмки
- Векторный поиск работает на Testcontainers (Postgres+pgvector); фильтр по грифу применяется на стороне БД.
