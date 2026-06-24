# Э3-01. `CoreDbContext` + схема `core` + миграции

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (код, миграция, тест; прогон на живой БД — при наличии Docker) |
| Требования ТЗ | ТО-инф-01, ТО-инф-03, ТО-прог-08, ТС-008 |
| Документы | [ДОК-04](../04_Схема_данных.md) |
| Зависит от | Э2-03, Э1-04, Э1-05 |

## Цель
Реализовать универсальный контекст данных ядра в схеме `core` с собственной историей миграций.

## Что сделать
- ⏳ `CoreDbContext` (схема `core`): документы, чанки, эмбеддинги, нормы/редакции НПА, аудит, пользователи/допуски, задания индексации.
- ⏳ Обязательные метаданные (гриф, подразделение — `NOT NULL`; редакция/статус) по ДОК-04.
- ⏳ Регистрация через `AddDbContextFactory` (не scoped, ТС-008).
- ⏳ Начальные миграции; порядок применения `core → inspector` (ТО-прог-08), идемпотентные скрипты.

## Критерии приёмки
- Миграции применяются на чистой БД; интеграционный тест на Testcontainers создаёт схему `core`.

## Результат
- **Базовая иерархия сущностей** в `Abstractions` (общая для ядра и профиля): `BaseEntity`, `IAuditableEntity`/`AuditableEntity`, `ISoftDeletable`/`SoftDeletableEntity`.
- Сущности `core` (суффикс `*Entity`): `DocumentEntity`, `ChunkEntity` — **generic-корпус**, аудируемые и **физически удаляемые** (ТБ-064); `AppUserEntity`, `ClearanceEntity` — **soft-delete**; `IndexingJobEntity`. Гриф/подразделение `NOT NULL`; на чанке — нейтральный флаг годности `IsCurrent` (ADR-0013). **Доменная модель НПА (`LegalNorm`/`NormRevision`/`RevisionStatus`) вынесена из ядра в профиль** (`Inspector.Domain`/`.Data`, схема `inspector`) — нейтральность ядра (ТС-003, ADR-0002).
- **Конфигурации — отдельными классами** `IEntityTypeConfiguration` в `EntityConfigurations/{Corpus,Users,Indexing}Configurations/`; подключены `ApplyConfigurationsFromAssembly` по namespace.
- Общий механизм аудита — в базовом [`AuditedDbContext`](../../src/core/ISC.AI.Persistence/AuditedDbContext.cs) (авто-таймстемпы, soft-delete, query-filter `!IsDeleted`); его наследуют оба контекста — `CoreDbContext` и `InspectorDbContext` профиля. [`CoreDbContext`](../../src/core/ISC.AI.Persistence/CoreDbContext.cs): `ApplyConfigurationsFromAssembly`; [design-фабрика](../../src/core/ISC.AI.Persistence/Design/CoreDbContextDesignFactory.cs); [`AddCorePersistence`](../../src/core/ISC.AI.Persistence/CorePersistenceServiceCollectionExtensions.cs) (фабрика, ТС-008) в `Program.cs`. Имена БД — **snake_case** (`EFCore.NamingConventions`; C# — PascalCase), таблицы со схемой `core`. Enum'ы ядра — в `Abstractions/Enums/` (`ModelRole`, `IndexingJobStatus`); `RevisionStatus` — в профиле.
- Миграция `InitialCore` (`src/core/ISC.AI.Persistence/Migrations/`) — generic-корпус без НПА; история миграций в схеме `core`. Доменная миграция профиля `InitialInspector` (`Inspector.Data/Migrations/`, схема `inspector`, своя история) с таблицами `legalNorm`/`normRevision`/`normDocumentLink`/`chunkRevisionLink` (FK только внутри `inspector`; связи с `core` — слабые по значению, ТО-инф-06). Папки миграций помечены как генерируемый код.
- Интеграционный тест на Testcontainers: [`CoreSchemaMigrationTests`](../../tests/ISC.AI.IntegrationTests/Persistence/CoreSchemaMigrationTests.cs) (применение миграции + `NOT NULL` грифа/подразделения). Требует Docker.
- Локальный инструмент `dotnet-ef` 10.0.9 (манифест `dotnet-tools.json`).
- Сборка решения — 0 ошибок, 0 предупреждений; unit-тесты — 6/6 (4 правила зависимостей + 2 теста нейтральности ядра, [`CoreNeutralityTests`](../../tests/ISC.AI.UnitTests/Architecture/CoreNeutralityTests.cs)).

## Осталось / зависимости
- Прогон интеграционного теста на живой БД — при наличии Docker.
- Векторное хранилище (pgvector) + `Embedding` — [Э3-02](Э3-02-pgvector.md); аудит — [Э3-03](Э3-03-аудит.md).
- Решения по сертифицируемости (ОС/СУБД/шифрование) — Э1-05 (ТБ-061), на код контекста не влияют.
