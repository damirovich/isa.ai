# Э3-07. Fail-closed загрузка (гриф/подразделение обязательны)

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (порт fail-closed, чанкер, дедуп, тесты; парсинг форматов — Э4-01; прогон — Docker) |
| Требования ТЗ | ТБ-024, ТО-инф-03, ТНД-002 |
| Документы | [ДОК-05 §2.8](../05_Контракты_и_композиция.md) |
| Зависит от | Э3-01 |

## Цель
Реализовать порт загрузки так, чтобы документ без явного грифа/подразделения **не попадал** в индекс.

## Что сделать
- ✅ Контракт `IIngestionPort` + `IngestionRequest`/`IngestionResult`/`ITextChunker` в `Abstractions`; конвейер `IngestionPort` в `ISC.AI.Ingestion`.
- ✅ Полный набор обязательных метаданных на каждый чанк + fail-closed: без явных грифа/подразделения — отказ, ничего не индексируется (ТБ-024).
- ✅ Идемпотентность/дедупликация по `ContentHash` (ТНД-002).

## Результат
- Контракты [`IIngestionPort`](../../src/core/ISC.AI.Abstractions/Ingestion/IIngestionPort.cs), `IngestionRequest` (гриф/подразделение `null`-овые — детектор «не задано» для fail-closed), `IngestionResult`, `ITextChunker`.
- [`IngestionPort`](../../src/core/ISC.AI.Ingestion/IngestionPort.cs): fail-closed (нет грифа/подразделения → `Reject` без записи); дедуп по `ContentHash`; чанкинг ([`SimpleTextChunker`](../../src/core/ISC.AI.Ingestion/SimpleTextChunker.cs)) → эмбеддинги (роль Embeddings) → запись `Document`/`Chunk`/`Embedding` в ОДНОЙ транзакции с денормализацией режима + `IsCurrent=true`. `AddCoreIngestion` в хосте.
- Тесты: unit [`IngestionTests`](../../tests/ISC.AI.UnitTests/Ingestion/IngestionTests.cs) (fail-closed + чанкер); интеграционный [`IngestionPipelineTests`](../../tests/ISC.AI.IntegrationTests/Persistence/IngestionPipelineTests.cs) (индексация с грифом, дедуп, отказ без грифа). Сборка 0/0; unit 22/22.

## Осталось
- Парсинг ФОРМАТОВ источников (OCR Tesseract для сканов, разбор `.docx`) и фоновая загрузка корпуса — [Э4-01](Э4-01-ingestion-корпус.md); порт принимает уже извлечённый текст.
- Прогон интеграционного теста — Docker/сервер с pgvector.
- Качественный чанкинг (перекрытие/семантика) — поверх `ITextChunker`.

## Критерии приёмки
- Интеграционный тест: в индексе нет чанков с неопределённым грифом; повтор загрузки не создаёт дублей.
