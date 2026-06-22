# Справочник: локальный LLM API (Gemma 4 · llama-server)

> **Источник.** Версионированная копия документации заказчика по локальному серверу ИИ
> (`D:\ЦИБ\InspektorAI\gemma-api-guide.docx`). Это **референс**, а не требования; требования — в [ТЗ](../ТЗ_ISC.AI.md), решения — в [ADR-0011](../06_ADR/ADR-0011-vybor-modeley-i-embedder.md).

## 1. Обзор

| Параметр | Значение |
|---|---|
| Движок | **ik_llama.cpp** (`llama-server`) |
| Модель | **Gemma 4 26B-A4B** (QAT, UD-Q4_K_XL) |
| API | OpenAI-совместимый |
| Base URL | `http://<SERVER_IP>:9000` |
| OpenAI-префикс | `/v1` |
| Авторизация | нет (запущен без `--api-key`) |
| Контекст | **до 65 536 токенов** (промпт + ответ суммарно) |
| Режим | text-only (vision-проектор не загружен) |

API OpenAI-совместимый — подходит любой клиент/SDK для OpenAI, нужно лишь подменить base URL. Поле `api_key` требуется формально — можно передавать строку-заглушку.

## 2. Эндпоинты

| Метод | Путь | Назначение |
|---|---|---|
| POST | `/v1/chat/completions` | **Основной** — чат с chat-шаблоном Gemma |
| POST | `/v1/completions` | Сырое продолжение текста (без chat-шаблона) |
| GET | `/v1/models` | Список моделей (id для поля `model`) |
| POST | `/v1/embeddings` | Эмбеддинги (см. оговорку в §6) |
| GET | `/health` | Жив ли сервер / статус загрузки модели |
| GET | `/props` | Текущая конфигурация сервера |
| GET | `/metrics` | Prometheus-метрики (только с `--metrics`) |
| POST | `/tokenize` · `/detokenize` | Текст ↔ токены |

Для платформы в ~95% случаев нужен только `/v1/chat/completions`.

## 3. Chat Completions

`POST /v1/chat/completions` — принимает список сообщений, применяет chat-шаблон Gemma, возвращает ответ ассистента.

**Тело запроса:**
```json
{
  "model": "gemma",
  "messages": [
    {"role": "system", "content": "Ты ассистент, отвечающий на кыргызском."},
    {"role": "user", "content": "Расскажи про Бишкек."}
  ],
  "temperature": 1.0,
  "top_p": 0.95,
  "top_k": 64,
  "max_tokens": 1024,
  "stream": false,
  "stop": ["<end_of_turn>"]
}
```

**Параметры:**

| Поле | Тип | Описание |
|---|---|---|
| `messages` | array | **Обязательное.** Диалог по порядку; роли `system`/`user`/`assistant`. |
| `model` | string | Формально обязательно для OpenAI-клиентов. Модель одна, значение не влияет на маршрутизацию. Точный id — в `/v1/models`. |
| `temperature` | float | Случайность. Для Gemma 4 рекомендуется `1.0`. |
| `top_p` | float | Nucleus sampling. Рекомендация `0.95`. |
| `top_k` | int | Ограничение топ-K. Рекомендация `64`. |
| `min_p` | float | Альтернатива `top_p`. |
| `max_tokens` | int | Максимум токенов в ответе. |
| `stream` | bool | `true` — стримить по токенам (§4). |
| `stop` | array | Строки, на которых обрывать генерацию. |
| `seed` | int | Фиксировать для воспроизводимости. |
| `repeat_penalty` | float | Штраф за повторы (по умолчанию выключен). |

**Ответ:**
```json
{
  "id": "chatcmpl-...",
  "object": "chat.completion",
  "model": "gemma",
  "choices": [
    { "index": 0,
      "message": { "role": "assistant", "content": "Бишкек — столица Кыргызстана..." },
      "finish_reason": "stop" }
  ],
  "usage": { "prompt_tokens": 24, "completion_tokens": 187, "total_tokens": 211 }
}
```

- Текст ответа: `choices[0].message.content`.
- `finish_reason`: `stop` (нормально), `length` (упёрлось в `max_tokens`).
- `usage` — счётчики токенов (логировать для мониторинга).

## 4. Стриминг

`"stream": true` → ответ кусками через SSE (`text/event-stream`):
```text
data: {"choices":[{"delta":{"content":"Биш"},"index":0}]}
data: {"choices":[{"delta":{"content":"кек"},"index":0}]}
data: [DONE]
```
Текст — в `choices[0].delta.content`; поток заканчивается `data: [DONE]`. Для UI/чата стриминг ощутимо улучшает отзывчивость.

## 5. Параметры сэмплинга

| Параметр | По умолч. | Что делает |
|---|---|---|
| `temperature` | — | Выше — разнообразнее; `0` ≈ жадный выбор. |
| `top_k` | 64 (рек.) | Оставить K самых вероятных токенов. |
| `top_p` | 0.95 (рек.) | Nucleus: токены на суммарную вероятность P. |
| `min_p` | — | Отсекать ниже доли от самого вероятного. |
| `repeat_penalty` | 1.0 (off) | Штраф за повтор. |
| `presence/frequency_penalty` | 0 | OpenAI-style штрафы. |

Рекомендация Google для Gemma 4: `temperature=1.0, top_p=0.95, top_k=64` (уже зашиты в сервер через `--samplers`, можно не передавать).

## 6. Особенности Gemma 4

**Reasoning (режим размышления).** Gemma 4 — hybrid-thinking: перед ответом может «подумать». Зависит от `--reasoning-format`:
- `inline` — мысли в `content`, обёрнутые в `<think>...</think>`;
- отдельным полем — мысли в `choices[0].message.reasoning_content`, в `content` только чистый ответ.

> **Для платформы:** нужны чистые ответы — либо `--reasoning off` на сервере, либо **вырезать `<think>...</think>` на своей стороне**. Reasoning полезен на сложных задачах, но добавляет токены/время — включать выборочно по типу задачи в оркестраторе.

**System prompt.** Поведение/персона — первым сообщением с ролью `system`.

**Мультимодальность.** Текущий сетап — text-only (vision-проектор не загружен).

**Эмбеддинги.** `/v1/embeddings` технически доступен, но **Gemma-instruct не заточена под качественные эмбеддинги**. Для RAG/поиска лучше поднять **отдельную embedding-модель (например, EmbeddingGemma) на втором инстансе/порту**.

## 7. Интеграция с .NET

**Вариант А — голый `HttpClient`** (без зависимостей): `POST /v1/chat/completions`, читать `choices[0].message.content`.

**Вариант Б — официальный OpenAI .NET SDK** (то, что использует ISC.AI через `Microsoft.Extensions.AI`):
```csharp
using OpenAI.Chat;
using OpenAI;
using System.ClientModel;

var client = new ChatClient(
    model: "gemma",
    credential: new ApiKeyCredential("not-needed"),       // авторизации нет — заглушка
    options: new OpenAIClientOptions { Endpoint = new Uri("http://<SERVER_IP>:9000/v1") }); // обязательно с /v1

ChatCompletion completion = await client.CompleteChatAsync(
    new SystemChatMessage("Ты технический ассистент."),
    new UserChatMessage("Что такое KV-кэш одним абзацем?"));
Console.WriteLine(completion.Content[0].Text);
```
Стриминг — `client.CompleteChatStreamingAsync(...)`, читать `update.ContentUpdate[i].Text`.

**Советы по интеграции:** один `HttpClient`/`ChatClient` как singleton; щедрые таймауты (prefill на CPU не мгновенный); логировать `usage.total_tokens` и `finish_reason`.

## 8. Prompt caching (скорость)

`llama-server` кэширует обработанный **префикс** промпта: если запросы начинаются одинаково (общий system prompt) — повторный prefill пропускается. **Вывод:** держать стабильный, неизменный system prompt в начале каждого запроса (совпадает со слоёной схемой промптов ISC.AI, ДОК-02 §9).

## 9. Эксплуатация

```bash
curl http://localhost:9000/health        # жив ли сервер
curl http://localhost:9000/props         # текущая конфигурация
curl http://localhost:9000/v1/models     # модели и их id

sudo systemctl status|restart gemma      # статус/перезапуск
journalctl -u gemma -f                    # живой лог
```

**Лимиты:**
- Контекст: до 65 536 токенов (промпт + ответ). Превышение — обрезка/ошибка.
- **Параллелизм: одна модель, последовательная обработка.** Несколько одновременных запросов встают в очередь.

**Авторизация (если открывать наружу):** `--api-key КЛЮЧ` → заголовок `Authorization: Bearer КЛЮЧ`, либо Caddy перед сервером (TLS + auth).

## 10. Коды ошибок

| Код | Значение | Что делать |
|---|---|---|
| 200 | OK | — |
| 400 | Плохой запрос (кривой JSON/параметры) | Проверить тело. |
| 401 | Нет/неверный ключ | Только при включённом `--api-key`. |
| 500 | Внутренняя ошибка | Часто chat-шаблон → флаг `--jinja`; смотреть `journalctl`. |
| 503 | Сервер грузит модель | Подождать и повторить (после старта/перезапуска). |

---

## 11. Выводы для платформы ISC.AI (что из этого следует)

| Факт из API | Следствие для нас | Где учтено |
|---|---|---|
| Движок — `ik_llama.cpp`/`llama-server`, не vLLM | Архитектура engine-нейтральна (`IChatClient`/OpenAI-совместимость) — менять не нужно; в доках движок назван точно | ADR-0011, ТО-прог-01 |
| Контекст **64K** (не 250K) | RAG обязателен — корпус НПА не влезает; следить за бюджетом контекста | ДОК-02 §8, RAG Э3-10 |
| Эмбеддинги — **отдельной моделью** (EmbeddingGemma, второй порт) | Роль `embeddings` — отдельный инстанс; НЕ использовать чат-модель | Э3-04, Э3-02, ADR-0011 |
| Reasoning `<think>` | Оркестратор вырезает `<think>` либо `--reasoning off`; включать reasoning выборочно | Э3-04, Э3-10 |
| Последовательная обработка (очередь) | НФТ: пропускная способность, тайм-ауты, возможно несколько инстансов | ТН-001/002 |
| Prompt caching на стабильном префиксе | Стабильный системный промпт в начале (уже в нашей слоёной схеме) | ДОК-02 §9 |
| Сэмплинг Gemma 4: `temp=1.0, top_p=0.95, top_k=64` | Дефолты для роли `draft`; уже на сервере | ADR-0011 |
| Авторизации нет; канал открыт | Канал «хост ↔ инференс» защищается обязательно (loopback/внутр. интерфейс) | ТБ-044 |

*Документ — справочный; при расхождении с реальной конфигурацией сервера приоритет у `/props` и `/v1/models`.*
