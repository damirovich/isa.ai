# Э1-03. Черновые контракты `Abstractions`

| | |
|---|---|
| Этап | Э1 — Проектирование (реализация — Э2/Э3) |
| Статус | ✅ Выполнено (все контракты реализованы в Э2/Э3–Э4; сверка 2026-08-25) |
| Требования ТЗ | ТО-прог-04 |
| Документы | [ДОК-05 Контракты и композиция](../05_Контракты_и_композиция.md) |
| Зависит от | Э0-01 |

## Цель
Зафиксировать программные контракты платформы в `ISC.AI.Abstractions` (только интерфейсы, без EF/Npgsql/OpenAI).

## Что сделать
- ✅ Композиционные контракты: `IProfile`, `IModule` (+`ModuleDescriptor`), `IModelContributor` (+`ModelRole`), `IPromptProvider` (+`PromptTemplate`) — см. [Э2-01](Э2-01-контракты-композиции.md).
- ✅ `IRetriever` + `AccessContext`/`RetrievedChunk` (фильтр доступа) — [Э3-05](Э3-05-retriever-фильтр-доступа.md).
- ✅ Контракт грунтовки (`IGroundingValidator` + `ICitationExtractor`/`ICitationNormalizer`) — [Э3-06](Э3-06-грунтовка.md).
- ✅ Порт ingestion (`IIngestionPort`) — [Э3-07](Э3-07-ingestion-fail-closed.md).
- ✅ Порт рендеринга документов (`IDocumentExporter`: экспорт .docx, маркировка грифа) — [Э4-04](Э4-04-экспорт-docx.md).
- ✅ Порт аудита (`IAuditWriter`) — [Э3-03](Э3-03-аудит.md).

## Критерии приёмки
- В `Abstractions` нет ссылок на EF/Npgsql/OpenAI (слой тонкий).
- Каждый контракт документирован XML-doc на русском (ТД-003), инварианты безопасности — явно.

## Результат
- `src/core/ISC.AI.Abstractions/**` — все контракты платформы (композиционные — Э2-01,
  retrieval/грунтовка/ingestion/аудит — Э3, экспорт — Э4-04); слой тонкий, без EF/Npgsql/OpenAI.
