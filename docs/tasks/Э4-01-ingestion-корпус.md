# Э4-01. Загрузка ограниченного корпуса (OCR→чанкинг→эмбеддинги)

| | |
|---|---|
| Этап | Э4 — MVP (P0) |
| Статус | 🔄 В работе (управляемое извлечение `.txt`/`.docx` + файл→порт готовы; OCR и структурный чанкинг НПА — далее) |
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

## Критерии приёмки
- Корпус проиндексирован; чанков без грифа нет; данные — только обезличенные/тестовые (до режим-гейта Э4-06).

## Результат (промежуточно)
- Нейтральные контракты [`ITextExtractor`/`IFormatTextExtractor`/`ExtractedDocument`](../../src/core/ISC.AI.Abstractions/Documents/) в `Abstractions`.
- Извлекатели [`PlainTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/PlainTextExtractor.cs), [`DocxTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/DocxTextExtractor.cs) + фасад [`CompositeTextExtractor`](../../src/core/ISC.AI.Documents/Extraction/CompositeTextExtractor.cs); `AddCoreDocuments`.
- Связующий [`FileIngestionService`](../../src/core/ISC.AI.Ingestion/FileIngestionService.cs) (`IFileIngestor`): файл → извлечение → `IngestionPort`; неподдержанный формат — явный отказ. Подключено в хост.
- Тесты: docx/txt/фасад/файл-ingestion. Сборка 0/0; unit 31/31.

## Осталось
- **OCR (Tesseract)**: `OcrTextExtractor : IFormatTextExtractor` для сканов/изображений + `tessdata` (rus/kir), предзагрузка в air-gap.
- **Структурный чанкинг НПА** — профиль (поверх `SimpleTextChunker`), при необходимости.
- **End-to-end прогон**: реальный `.docx` → конвейер → корпус на Testcontainers + эмбеддер (live llama-server или фейк).
