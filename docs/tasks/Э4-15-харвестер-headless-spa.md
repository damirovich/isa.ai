# Э4-15. Харвестер: источники-SPA через headless-рендеринг (JS-сайты вроде ЦБД Минюста)

| | |
|---|---|
| Этап | Э4 — сборщик корпуса (вне контура) |
| Статус | ✅ Реализовано (IPageFetcher + HeadlessPageFetcher/Playwright + флаг RenderMode + пресет ЦБД + тесты; сборка 0/0, unit 59/59). Живой прогон на SPA + калибровка селекторов — операционно |
| Требования ТЗ | ТО-инф-07 (обновление знаний = загрузка документа), ПОДГ-02 (первичная загрузка корпуса); air-gap ТБ-052 (харвестер ВНЕ контура) |
| Источник | Разбор реального источника НПА — [cbd.minjust.gov.kg](https://cbd.minjust.gov.kg/ru) |
| Зависит от | Э4-08 (движок харвестера: `ISourceConnector`, `ConfigurableSiteConnector`, `SiteRules`) |
| ADR | Обновить ADR-0015 (источники: статический HTML vs SPA/headless vs API) |

## Контекст
Реальный авторитетный источник НПА — **ЦБД Минюста КР** (`cbd.minjust.gov.kg`) — оказался **JavaScript-приложением (SPA)**: сервер отдаёт пустой каркас, контент (каталог, текст актов, реестр) подгружается скриптами. Проверено фетчами: и `/notional-register-npa/ru`, и `/act/view/ru-ru/{id}` в статическом HTML пусты.

Наш харвестер ([`ConfigurableSiteConnector`](../../src/tools/ISC.AI.Harvester/Connectors/ConfigurableSiteConnector.cs), [`HtmlContentExtractor`](../../src/tools/ISC.AI.Harvester/Engine/HtmlContentExtractor.cs)) читает **статический HTML** (AngleSharp, без выполнения JS) → на SPA находит пустоту (0 документов). Существующий пресет рассчитан на статический `www.gov.kg`, но не на ЦБД.

> **Предпочтительная альтернатива — официальный API.** ЦБД интегрирован в **Tunduk** (госшину); заказчик — госорган и может получить легальный доступ к API Минюста (чистый JSON с редакциями, ru/ky). Headless — **надёжный запасной путь**, если API недоступен. Оценить API-путь ДО реализации headless (может стать отдельной задачей — API-коннектор).

## Цель
Научить харвестер собирать документы с **JS-рендерящихся (SPA)** источников через **headless-браузер**, НЕ переписывая универсальный селекторный движок: меняется только шаг «получить HTML страницы».

## Что сделать
- ⏳ Абстракция `IPageFetcher` (Engine): `Task<string> GetHtmlAsync(url, ct)` — «как достать HTML».
- ⏳ `HttpPageFetcher` — текущее поведение (`HttpClient.GetStringAsync`), режим `Static`, быстрый (для gov.kg и обычных сайтов).
- ⏳ `HeadlessPageFetcher` — рендер JS через **Playwright** (Chromium): `GotoAsync(WaitUntil=NetworkIdle)` (+ опц. `WaitForSelectorAsync(ReadySelector)`) → `ContentAsync()` (готовый DOM). Один браузер на прогон (не на страницу).
- ⏳ `ConfigurableSiteConnector` зависит от `IPageFetcher` (замена прямого `httpClient.GetStringAsync` — одна строка); селекторы/пагинация/бандл **без изменений**.
- ⏳ Конфиг: `RenderMode` (`Static`|`Headless`) + `ReadySelector` в `SiteRules`/`SourceConfig`; движок выбирает fetcher по режиму.
- ⏳ Пресет `cbd.minjust` в `SitePresets` (реальные селекторы `/act/view/...`, заголовок, тело; калибруется оператором на живом сайте).
- ⏳ Обновить ADR-0015 (три класса источников: статический HTML, SPA/headless, API).
- ⏳ Тест: коннектор с `IPageFetcher`-заглушкой, отдающей «отрендеренный» HTML, извлекает документы теми же селекторами (без реального браузера в CI).

## Критерии приёмки
- Харвестер с `RenderMode=Headless` извлекает документы с JS-рендерящейся страницы (заголовок+текст непусты).
- Статический путь (`RenderMode=Static`, gov.kg) продолжает работать без изменений.
- Ядро/контур не затронуты (Playwright — только в проекте харвестера, вне контура).

## Осталось / риски
- **Зависимость:** `Microsoft.Playwright` (лицензия **MIT** ✅) + бинарь Chromium (~150 МБ, `playwright install`). Только в харвестере (вне контура) — режим/air-gap не нарушается.
- **Скорость:** headless в разы медленнее HTTP → вежливые паузы, лимит страниц, переиспользование браузера.
- **Пагинация кликом:** если «следующая» — JS-кнопка, а не `<a href>`, `NextPageSelector` её не пройдёт → потребуется расширение «действие-клик» (отдельно).
- **Хрупкость:** скрапинг ломается при смене вёрстки сайта → ещё один довод в пользу API-пути.
- Связано с Э4-08 (движок), ADR-0015; альтернатива — API-коннектор ЦБД (через Tunduk).

## Результат (реализовано)
- Абстракция [`IPageFetcher`](../../src/tools/ISC.AI.Harvester/Engine/IPageFetcher.cs) + [`IPageFetcherFactory`](../../src/tools/ISC.AI.Harvester/Engine/IPageFetcherFactory.cs): «как достать HTML» отделено от селекторов.
- [`HttpPageFetcher`](../../src/tools/ISC.AI.Harvester/Engine/HttpPageFetcher.cs) (Static, текущее поведение) и [`HeadlessPageFetcher`](../../src/tools/ISC.AI.Harvester/Engine/HeadlessPageFetcher.cs) (Playwright/Chromium: `Goto`→network-idle→опц. `WaitForSelector`→`ContentAsync`; браузер один на прогон).
- Флаг [`RenderMode`](../../src/tools/ISC.AI.Harvester/Engine/RenderMode.cs) + `ReadySelector` в [`SiteRules`](../../src/tools/ISC.AI.Harvester/Engine/SiteRules.cs); [`ConfigurableSiteConnector`](../../src/tools/ISC.AI.Harvester/Connectors/ConfigurableSiteConnector.cs) выбирает fetcher по режиму (селекторы/пагинация/бандл — без изменений). `GenericUrlConnector` — статический.
- Пресет `cbd.minjust` (RenderMode=Headless) в [`SitePresets`](../../src/tools/ISC.AI.Harvester/Engine/SitePresets.cs) — **селекторы предварительные, калибруются на живом сайте**.
- Playwright (MIT) в CPM + csproj харвестера. Изоляция вне контура сохранена (арх-тест зелёный).
- Тесты: headless-режим и извлечение из «отрисованного» HTML — [`ConfigurableSiteConnectorTests`](../../tests/ISC.AI.UnitTests/Harvester/ConfigurableSiteConnectorTests.cs) + фейк [`FakePageFetcher`](../../tests/ISC.AI.UnitTests/Harvester/FakePageFetcher.cs). Сборка 0/0; unit 59/59.
- ⚠️ **Перед первым живым прогоном:** `playwright install chromium` (ставит бинарь браузера). Живой прогон на cbd.minjust + калибровка селекторов/`ReadySelector` — операционно (реальный DOM в CI не гоняем).

### Не вошло (осознанно)
- Селекторы ЦБД — предварительные (нужен отрисованный DOM живого сайта); UI-тумблер RenderMode для ручного ввода правил — опционально (через пресет уже работает); клик-пагинация SPA (если «следующая» — JS-кнопка) — отдельно; API-путь (Tunduk) — предпочтительная альтернатива, отдельной задачей.
