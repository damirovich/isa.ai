using System.Runtime.CompilerServices;

// Открывает internal-члены для интеграционных тестов (напр. AuditWriter.ComputeRecordHash — тест
// обнаружения подмены пересчитывает record_hash из прочитанных из БД полей той же продовой логикой, ТБ-031).
[assembly: InternalsVisibleTo("ISC.AI.IntegrationTests")]
