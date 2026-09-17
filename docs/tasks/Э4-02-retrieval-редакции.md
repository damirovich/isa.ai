# Э4-02. Retrieval с фильтром доступа и статуса редакции

| | |
|---|---|
| Этап | Э4 — MVP (P0) |
| Статус | 🔄 Механизм готов (порт годности ядра + материализатор профиля + write-side GATE-3, тесты 0/0); наполнение домена редакций из реальных НПА — позже |
| Требования ТЗ | ТБ-020, ТБ-021, ТФ-НПА-02, ТО-инф-04 |
| Документы | [ДОК-05 §2.6](../05_Контракты_и_композиция.md), [ДОК-04 §6](../04_Схема_данных.md), [ADR-0013](../06_ADR/) |
| Зависит от | Э3-05, Э4-01 |

## Цель
Извлекать релевантные фрагменты с учётом допуска (ядро) и актуальности источника (нейтральный флаг `IsCurrent`). Доменное сопоставление «действующая редакция НПА → `IsCurrent`» — на стороне профиля (схема `inspector`), поверх generic-retrieval ядра; ядро про НПА не знает.

## Что сделать
- ✅ Ядровый `IRetriever` применён в сценариях (Э3-05 / Э4-07): фильтр доступа (fail-closed) + только актуальные `IsCurrent`.
- ✅ Профиль материализует статус редакции → `core.chunk.is_current` по `chunk_revision_link`: нейтральный порт ядра `IChunkCurrencyPort` + доменная служба `IRevisionStatusMaterializer` (ADR-0013).
- ✅ Пометка «УТРАТИЛА СИЛУ» — в UI «База НПА» (Э4-07) для неактуальных.

## Критерии приёмки
- **GATE-3**: утратившая силу не выдаётся как действующая; пользователь не получает материалы выше допуска (GATE-1).

## Результат
- Ядро: [`IChunkCurrencyPort`](../../src/core/ISC.AI.Abstractions/Corpus/IChunkCurrencyPort.cs) + [`ChunkCurrencyPort`](../../src/core/ISC.AI.Persistence/Corpus/ChunkCurrencyPort.cs) — согласованно ставит `is_current` чанку и эмбеддингу (одной транзакцией).
- Профиль: [`IRevisionStatusMaterializer`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Domain/Services/IRevisionStatusMaterializer.cs) (домен) + [`RevisionStatusMaterializer`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Data/RevisionStatusMaterializer.cs) (данные): по `ChunkRevisionLink` находит чанки редакции → ставит видимость → фиксирует статус (и дату утраты силы). Use-case [`SetRevisionStatusCommand`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Application/Features/Norms/Commands/SetRevisionStatus/SetRevisionStatusCommand.cs) — с картотекой (2026-08-10) получил гард ведения (`NormGuard`) и аудит (`IAuditableRequest`), а материализатор — два ограждения: поднятие не воскрешает погашенные заменой (supersede) документы, гашение не прячет чанки, действующие по другой редакции.
- Тесты: handler (unit), [`ChunkCurrencyPortTests`](../../tests/ISC.AI.IntegrationTests/Persistence/ChunkCurrencyPortTests.cs), [`RevisionStatusMaterializerTests`](../../tests/ISC.AI.IntegrationTests/Persistence/RevisionStatusMaterializerTests.cs) (**write-side GATE-3**, две схемы в одной БД). Сборка 0/0; unit 51/51; интеграционные 9/9 (на живом Postgres).
- **Порядок РЕЖИМНО-безопасный:** видимость в ядре материализуется ДО сохранения статуса (durable hide) — безопаснее единой транзакции (которая при откате оставила бы устаревшее видимым). Полноценная единая транзакция через два контекста — рефайнмент.

## Осталось
- ~~**Наполнение домена редакций**~~ — РУЧНОЕ наполнение закрыто картотекой НПА (2026-08-10,
  ветка `feature/npa-registry`): экраны `/norms` и `/norms/{id}` создают нормы/редакции и привязывают
  документы корпуса (`NormDocumentLink` + `ChunkRevisionLink`); привязка к утратившей силу редакции
  гасит чанки сразу (hide-first). Остаётся АВТОМАТИЧЕСКОЕ наполнение из метаданных загрузки
  (documentCode/editionId из пакетов ЦБД лежат в `core.document.metadata` непрочитанными).
