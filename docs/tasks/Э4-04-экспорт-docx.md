# Э4-04. Экспорт `.docx` с маркировкой грифа

| | |
|---|---|
| Этап | Э4 — MVP (P0) |
| Статус | 🔄 Ядро готово (порт+OpenXml-экспортёр+use-case+аудит+тесты, 0/0); UI-кнопка скачивания — далее |
| Требования ТЗ | ТФ-ГЕН-03, ТБ-033, ТБ-032 |
| Документы | [ДОК-05 §2.9](../05_Контракты_и_композиция.md), [ДОК-07 §11.4](../07_Руководство_администратора.md) |
| Зависит от | Э4-03 |

## Цель
Экспортировать сгенерированный документ в `.docx` с обязательной маркировкой грифа.

## Что сделать
- ✅ Порт `IDocumentExporter` в `Abstractions`; реализация `DocxDocumentExporter` в `ISC.AI.Documents` (OpenXml).
- ✅ Экспорт `.docx` из данных результата (заголовок/тело/реквизиты). *Программно; letterhead-шаблон профиля — refinement.*
- ✅ Автомаркировка грифа — в теле и в метаданных; гриф = `ResultClassification` (наследован, =max грифов фрагментов, ТБ-033/032).
- ✅ Фиксация экспорта в аудите (`AuditAction.Export`, ТБ-030).
- ⏳ UI: кнопка «Экспорт в .docx» на странице Генератора (скачивание через JS-interop).

## Критерии приёмки
- В теле и метаданных `.docx` проставлен гриф; экспорт залогирован; гриф не ниже максимума использованных фрагментов.

## Результат
- [`IDocumentExporter`](../../src/core/ISC.AI.Abstractions/Documents/IDocumentExporter.cs) + [`DocxDocumentExporter`](../../src/core/ISC.AI.Documents/Export/DocxDocumentExporter.cs): гриф видимой строкой в теле (верх, жирным) + в `PackageProperties` (Category/Keywords); пометка HITL «ЧЕРНОВИК».
- [`ExportReferenceCommand`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Application/Generation/ExportReferenceCommand.cs): гриф→маркировка ([`ClassificationMarking`](../../src/profiles/inspector/ISC.AI.Profile.Inspector.Application/Common/ClassificationMarking.cs), режимная схема — профиль), экспорт, аудит `Export`, файл в `ResponseDto`.
- Тесты [`DocxDocumentExporterTests`](../../tests/ISC.AI.UnitTests/Documents/DocxDocumentExporterTests.cs) (открывает .docx → гриф в теле+метаданных), [`ExportReferenceHandlerTests`](../../tests/ISC.AI.UnitTests/Profiles/ExportReferenceHandlerTests.cs) (маркировка+аудит). Сборка 0/0; unit 38/38.

## Осталось
- **UI-кнопка «Экспорт в .docx»** на странице Генератора + скачивание (Blazor JS-interop `downloadFileFromStream`) — нужен живой клик-тест.
- Опционально: letterhead-шаблон профиля (бланк/реквизиты) поверх программного рендера.
