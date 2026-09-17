# Э4-01. Загрузка ограниченного корпуса (OCR→чанкинг→эмбеддинги)

| | |
|---|---|
| Этап | Э4 — MVP (P0) |
| Статус | ✅ Загрузка операционно готова (извлечение `.txt`/`.docx` + страница «Загрузка корпуса» + импорт пакета; OCR и структурный чанкинг НПА — опциональные расширения) |
| Требования ТЗ | ТФ-НПА (корпус), ТО-инф-03, ТБ-024, ПОДГ-02 |
| Документы | [ДОК-02 §10](../02_Архитектура.md) |
| Зависит от | Э3-07, Э3-04 |

## Цель
Загрузить ограниченный (обезличенный) корпус НПА в индекс: OCR/парсинг → чанкинг → эмбеддинги → запись с метаданными.

## Что сделать
- ✅ Парсинг `.docx` (OpenXml) + `.txt`; нейтральные контракты извлечения (`ITextExtractor`).
- ⏳ OCR сканов/изображений (Tesseract) — нативные либы + `tessdata` (rus/kir), предзагрузка в контур.
- ✅ Векторизация (эмбеддинги) и generic-чанкинг — через `IngestionPort` (Э3-07).
- ⏳ Чанкинг по структуре НПА — на стороне профиля (поверх generic-чанкера ядра).
- ✅ Запись с обязательными метаданными, fail-closed (ТБ-024) — в порту.
- ✅ Операционная загрузка: страница «Загрузка корпуса» (файлы `.txt`/`.docx` + импорт пакета Harvester) + use-cases `IngestFileCommand`/`ImportBundleCommand`.

## Критерии приёмки
- Корпус проиндексирован; чанков без грифа нет; данные — только обезличенные/тестовые (до режим-гейта Э4-06).

## Результат (промежуточно)
- Нейтральные контракты [`ITextExtractor`/`IFormatTextExtractor`/`ExtractedDocument`](../../src/core/ISC.AI.Abstractions/Documents/) в `Abstractions`.
- Извлекатели [`PlainTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/PlainTextExtractor.cs), [`DocxTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/DocxTextExtractor.cs) + фасад [`CompositeTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/CompositeTextExtractor.cs); `AddCoreDocuments`.
- Связующий [`FileIngestionService`](../../src/core/ISC.AI.Ingestion/FileIngestionService.cs) (`IFileIngestor`): файл → извлечение → `IngestionPort`; неподдержанный формат — явный отказ. Подключено в хост.
- Тесты: docx/txt/фасад/файл-ingestion. Сборка 0/0; unit 31/31.

## Результат (операционная загрузка)
- Use-cases [`IngestFileCommand`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Application/Loading/IngestFileCommand.cs) (файл → `IFileIngestor`) и [`ImportBundleCommand`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Application/Loading/ImportBundleCommand.cs) (пакет Harvester → `IBundleImporter`).
- Страница [`CorpusLoad.razor`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.UI/CorpusLoad.razor) («Загрузка корпуса»): загрузка файлов с грифом/подразделением + импорт пакета; модуль в реестре. Сборка 0/0; unit 54/54.

## Осталось
- **Боевой прогон оператором:** применить миграции к рабочей БД → загрузить документы через страницу «Загрузка» → проверить «База НПА»/«Генератор» (нужен живой эмбеддер 9001).
- **OCR (Tesseract)** для сканов/изображений (`tessdata` rus/kir, предзагрузка в air-gap) — расширение.
- **Структурный чанкинг НПА** — профиль (поверх `SimpleTextChunker`) — расширение.
