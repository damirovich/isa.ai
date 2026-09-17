# Э3-09. IntegrationTests на Testcontainers + GATE-1/2/3

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (gate-набор собран и помечен; прогон интеграционных GATE на Docker/CI — при доступности Docker) |
| Требования ТЗ | ТО-прог-07, GATE-1, GATE-2, GATE-3, КИ-02 |
| Документы | [ДОК-09 §4](../09_Программа_и_методика_испытаний.md) |
| Зависит от | Э3-03, Э3-05, Э3-06, Э3-01 |

## Цель
Завести интеграционные тесты на настоящем Postgres+pgvector и блокирующие gate-тесты.

## Что сделать
- ✅ Базовая инфраструктура тестов на `Testcontainers.PostgreSql` (без EF InMemory — правило проекта).
- ✅ **GATE-1** — фильтр доступа по грифу/подразделению на стороне БД; нет доступа = пусто (неразличимость по контенту).
- ✅ **GATE-2** — грунтовка: ссылка вне актуальных фрагментов → не подтверждена (вывод не «готов»).
- ✅ **GATE-3** — версионность редакций: утратившая силу не выдаётся как действующая (КИ-02); видна только при `IncludeSuperseded` (ТЭ-003).
- ✅ Единый трейт `Category=Gate` — блокирующий набор запускается как одно целое.

## Критерии приёмки
- Все три gate-теста проходят безусловно; прогон в CI.

## Результат
Блокирующий gate-набор помечен трейтом `Category=Gate` — запуск как единого целого:
`dotnet test ISC.AI.slnx --filter "Category=Gate"` (подтверждено `--list-tests`: 5 случаев).
- **GATE-1** — [`RetrieverAccessFilterTests`](../../tests/ISC.AI.IntegrationTests/Persistence/RetrieverAccessFilterTests.cs) (интеграционный, Testcontainers): выше допуска / чужое подразделение / утратившее силу не выдаётся; без доступа — пусто.
- **GATE-2** — [`GroundingValidatorTests`](../../tests/ISC.AI.UnitTests/Grounding/GroundingValidatorTests.cs) (юнит): Confirmed / Unverified / Superseded; неподтверждённая ссылка ⇒ вывод не «готов». Сквозной вызов грунтовки в конвейере — [`GroundedGeneratorTests`](../../tests/ISC.AI.UnitTests/Rag/GroundedGeneratorTests.cs).
- **GATE-3** — [`Gate3RevisionVisibilityTests`](../../tests/ISC.AI.IntegrationTests/Persistence/Gate3RevisionVisibilityTests.cs) (интеграционный, Testcontainers): утратившая силу не выдаётся как действующая (КИ-02 = 0); включается только явным `RetrievalFilter(IncludeSuperseded: true)`.

Инфраструктура — реальный PostgreSQL+pgvector через `Testcontainers.PostgreSql`. Сборка 0/0; юнит 24/24.

## Осталось
- **Прогон интеграционных GATE-1/GATE-3 «зелёными»** требует Docker (как и прочие интеграционные тесты проекта) — выполнить на машине/CI с Docker.
- **Неразличимость по времени/тексту ошибки** (timing/oracle, ТБ-022) — отдельная методика поверх «неразличимости по контенту»; вынести в калибровку.
- **Прогон в CI** — подключить шаг `dotnet test --filter "Category=Gate"` в конвейер CI проекта (gate как блокирующий).
- End-to-end GATE-2 через оркестратор на реальном корпусе (фейк-модель с галлюцинацией ссылки) — при наличии Docker.
