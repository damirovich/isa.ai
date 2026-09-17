# Э3-04. `IChatClient`→vLLM, keyed-роли, эмбеддинги

| | |
|---|---|
| Этап | Э3 — Инфраструктурный фундамент |
| Статус | ✅ Выполнено (адаптер, keyed-роли, конфиг, хост, тесты; прогон на живом сервере — операционно) |
| Требования ТЗ | ТО-прог-01, ТО-прог-02, ТО-прог-03, ТБ-044 |
| Документы | [ДОК-02 §8](../02_Архитектура.md), [ДОК-06 ADR-0004/0011](../06_ADR/README.md) |
| Зависит от | Э2-03 |

## Цель
Подключить локальные модели через нейтральный `IChatClient` (Microsoft.Extensions.AI) к vLLM по keyed-ролям.

## Что сделать
- ✅ Адаптер к локальному OpenAI-совместимому серверу (llama-server) в `ISC.AI.AI`; keyed-регистрация draft/analysis/embeddings по `ModelRole` (расширение `AddCoreAiModels`, вызывается хостом; `IModelContributor` — шов для профиль-специфичных привязок).
- ✅ `IEmbeddingGenerator` — отдельная модель/порт (выбор эмбеддера — бенчмарк, ADR-0011).
- ✅ Защита канала «хост ↔ сервер»: endpoint из конфига, внутренний контур, без внешнего доступа (ТБ-044).
- ✅ Стриминг длинных вызовов — штатно `IChatClient.GetStreamingResponseAsync` (`IAsyncEnumerable` + `CancellationToken`).

## Результат
- Адаптер ядра [`AddCoreAiModels`](../../src/core/ISC.AI.AI/Models/CoreAiModelsServiceCollectionExtensions.cs): keyed по `ModelRole` — `IChatClient` (Draft/Analysis) и `IEmbeddingGenerator` (Embeddings); клиенты строятся `OpenAIClient(endpoint).GetChatClient/GetEmbeddingClient(...).AsIChatClient()/AsIEmbeddingGenerator()`. **Имена моделей и адреса — только из конфига** `Llm:Models:{role}` (ядро не знает названий — ТО-прог-02).
- Подключено в хосте ([Program.cs](../../src/core/ISC.AI.Web/Program.cs)): `AddCoreAiModels(...)`; секция `Llm` в [appsettings](../../src/core/ISC.AI.Web/appsettings.json) с внутренними endpoint'ами; эмбеддинги — отдельный порт/модель.
- Тесты: [`ModelRegistrationTests`](../../tests/ISC.AI.UnitTests/Ai/ModelRegistrationTests.cs) — keyed-резолв по роли + fail-closed для несконфигурированной роли. Сборка 0/0; unit 12/12.
- Примечание: базовая привязка моделей — core-расширением (хост его вызывает), т.к. правило зависимостей запрещает профилю ссылаться на `ISC.AI.AI`; `IModelContributor` остаётся швом для профиль-специфичных привязок (ADR-0004).

## Осталось
- Прогон на живом сервере инференса (`10.10.0.115`) — операционно; коннект проверен спайком `LlmChat`.
- Вырезание `<think>` (reasoning) / выборочный reasoning — на оркестраторе (Э3-10).
- Уточнить реальный порт/модель эмбеддера (EmbeddingGemma) в `appsettings` (сейчас плейсхолдер `:9001`).

## Критерии приёмки
- Ядро не знает названий моделей; клиент разрешается по ключу-роли; обращения только внутри контура.
