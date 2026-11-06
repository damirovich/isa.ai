using ISC.AI.Profile.Investigation.Domain.Entities;

namespace ISC.AI.Profile.Investigation.Domain.Services;

/// <summary>Отбор пакета обмена (ТФ-ОБМ-01): период, номера дел (пусто — дела с движением за период), виды носителей.</summary>
/// <param name="Year">Год.</param>
/// <param name="Month">Месяц, 1–12.</param>
/// <param name="CaseNumbers">Номера дел; пусто — все дела с движением за период.</param>
/// <param name="MediaKinds">Виды носителей, файлы которых едут в пакете, — имена пакета «Медиа»: Image, Video, Audio.</param>
/// <param name="ResendFiles">Отправить файлы и тех носителей, что ушли прежними пакетами (носитель обмена потерян).</param>
public sealed record ExchangeExportRequest(
    int Year, int Month, IReadOnlyList<string> CaseNumbers, IReadOnlyList<string> MediaKinds, bool ResendFiles = false);

/// <summary>Готовность экземпляра к обмену: что настроено и что нет.</summary>
/// <param name="InstanceId">Номер экземпляра (<c>Exchange:InstanceId</c>).</param>
/// <param name="TargetFolder">Носитель обмена.</param>
/// <param name="RecipientKeyFingerprint">Отпечаток ключа получателя (им шифруется пакет).</param>
/// <param name="SigningKeyFingerprint">Отпечаток ключа подписи этого экземпляра — его сообщают получателю.</param>
/// <param name="Problems">Что не так; пусто — можно формировать пакет.</param>
public sealed record ExchangeReadiness(
    string? InstanceId,
    string? TargetFolder,
    string? RecipientKeyFingerprint,
    string? SigningKeyFingerprint,
    IReadOnlyList<string> Problems)
{
    /// <summary>Можно формировать пакет.</summary>
    public bool Ready => Problems.Count == 0;
}

/// <summary>Строка журнала выгрузок.</summary>
public sealed record ExchangeExportRow(
    int Id,
    DateTime StartedAt,
    DateTime? FinishedAt,
    ExchangeExportStatus Status,
    int StartedByUserId,
    int Year,
    int Month,
    string? CaseNumbers,
    string MediaKinds,
    string? PackageName,
    int Cases,
    int Persons,
    int Links,
    int Reports,
    int Assets,
    int Files,
    int Appearances,
    long BytesWritten,
    short? Classification,
    string? Error);

/// <summary>Экран обмена: готовность, последние выгрузки и ход текущей.</summary>
/// <param name="Readiness">Готовность.</param>
/// <param name="Recent">Последние выгрузки, свежие сверху.</param>
/// <param name="RunningBytes">Сколько уже записано текущей выгрузкой; <see langword="null"/> — выгрузки нет.</param>
public sealed record ExchangeOverview(ExchangeReadiness Readiness, IReadOnlyList<ExchangeExportRow> Recent, long? RunningBytes);

/// <summary>Итог запуска выгрузки.</summary>
/// <param name="ExportId">Номер выгрузки; <see langword="null"/> — не запущена.</param>
/// <param name="Error">Почему не запущена.</param>
public sealed record ExchangeStartResult(int? ExportId, string? Error);

/// <summary>
/// Обмен между офисами — сторона источника (ТФ-ОБМ-01, ADR-0068): готовность, журнал выгрузок, запуск фонового
/// формирования пакета. Реализует слой данных профиля.
/// </summary>
/// <remarks>
/// ИНВАРИАНТ (ТБ-079, ТБ-083): пароль ключа подписи передаётся только в <see cref="StartAsync"/> и живёт в памяти задания
/// до подписи манифеста — в базу, журнал аудита и логи он не попадает. Право на действие проверяет сценарий (матрица
/// доступа «Обмен между офисами»), порт его не проверяет.
/// </remarks>
public interface IExchangeExport
{
    /// <summary>Готовность, последние выгрузки и ход текущей.</summary>
    Task<ExchangeOverview> GetOverviewAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Запускает формирование пакета в фоне. Одна выгрузка в каждый момент; не готов экземпляр, неверный пароль ключа
    /// подписи или выгрузка уже идёт — отказ с причиной.
    /// </summary>
    Task<ExchangeStartResult> StartAsync(
        ExchangeExportRequest request, int userId, string signingKeyPassword, CancellationToken cancellationToken = default);
}
