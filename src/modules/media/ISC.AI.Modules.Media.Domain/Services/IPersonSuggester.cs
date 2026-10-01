using System;
using System.Threading;
using System.Threading.Tasks;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Domain.Services;

/// <summary>Итог автоматического предложения (ТФ-ПЕР-09): по носителю или по всем материалам дела.</summary>
/// <param name="CasesChecked">В скольких делах искали (открытые, с основанием и эталонами).</param>
/// <param name="ReferencesChecked">Сколько сравнений «эталон × носитель» выполнено.</param>
/// <param name="SessionsCreated">Сколько сессий-предложений создано (по одной на эталон с совпадениями на носителе).</param>
/// <param name="CandidatesCreated">Сколько кандидатов «предложено системой» поставлено в очередь верификации.</param>
/// <param name="AssetsChecked">Сколько носителей сверено (для сверки дела; у сверки носителя — 1 или 0).</param>
public sealed record PersonSuggestionResult(
    int CasesChecked, int ReferencesChecked, int SessionsCreated, int CandidatesCreated, int AssetsChecked = 0)
{
    /// <summary>Ничего не сделано (функция выключена, у носителя нет подходящих дел или эталонов).</summary>
    public static PersonSuggestionResult None { get; } = new(0, 0, 0, 0);
}

/// <summary>
/// Автоматическое предложение связей с фигурантами (ТФ-ПЕР-09, ADR-0035): лица носителя сравниваются с эталонами
/// фигурантов того же дела, совпадения ставятся в очередь верификации кандидатами «предложено системой».
/// Подтверждает только человек, двумя подписями (ТБ-073); запуск и результат — в аудите (ТБ-072).
/// </summary>
public interface IPersonSuggester
{
    /// <summary>
    /// Сверить лица носителя с эталонами фигурантов всех его дел. Вызывает фоновый конвейер после индексации
    /// (<see cref="SuggestionTrigger.Indexing"/>) или фоновая задача по кнопке сотрудника (<see cref="SuggestionTrigger.Manual"/>).
    /// </summary>
    Task<PersonSuggestionResult> SuggestAsync(
        int assetId, SuggestionTrigger trigger = SuggestionTrigger.Indexing, CancellationToken cancellationToken = default);

    /// <summary>
    /// Сверить все обработанные материалы дела с эталонами его фигурантов: в деле появился эталон или основание
    /// поиска, а материалы загружены раньше (<see cref="SuggestionTrigger.CaseSweep"/>).
    /// </summary>
    Task<PersonSuggestionResult> SuggestForCaseAsync(int caseId, CancellationToken cancellationToken = default);
}

/// <summary>
/// Постановка сверки в фоновую очередь (ТФ-ПЕР-09): страница и команды профиля не ждут сравнения лиц. Сбой
/// постановки не роняет вызвавшую операцию (эталон или основание уже сохранены) — он пишется в лог, а сверку
/// можно запустить кнопкой «Сверить сейчас».
/// </summary>
public interface IPersonSuggestionScheduler
{
    /// <summary>Поставить сверку всех материалов дела; <c>null</c> — функция выключена или постановка не удалась.</summary>
    Task<Guid?> ScheduleCaseSweepAsync(int caseId, CancellationToken cancellationToken = default);

    /// <summary>Поставить сверку одного носителя; <c>null</c> — функция выключена или постановка не удалась.</summary>
    Task<Guid?> ScheduleAssetAsync(int assetId, SuggestionTrigger trigger, CancellationToken cancellationToken = default);
}
