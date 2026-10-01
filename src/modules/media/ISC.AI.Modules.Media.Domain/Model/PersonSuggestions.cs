using System;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Что запустило сверку носителя с эталонами фигурантов (ТФ-ПЕР-09, ADR-0035): обработка нового носителя,
/// появление эталона или основания поиска в деле (сверка всех материалов дела) либо кнопка сотрудника.
/// </summary>
public enum SuggestionTrigger
{
    /// <summary>Носитель обработан (индексация или переиндексация лиц).</summary>
    Indexing = 1,

    /// <summary>В деле появился эталон фигуранта или основание поиска — сверены уже обработанные материалы дела.</summary>
    CaseSweep = 2,

    /// <summary>Сотрудник нажал «Сверить сейчас» на карточке носителя.</summary>
    Manual = 3,
}

/// <summary>
/// Итог одной сверки носителя с эталонами фигурантов одного дела (ТФ-ПЕР-09) — для записи. Режимные поля — дела
/// (ТБ-070): по ним запись читается под решёткой. Биометрии в записи нет — только числа.
/// </summary>
/// <param name="AssetId">Носитель.</param>
/// <param name="CaseId">Дело, с фигурантами которого сверялся носитель.</param>
/// <param name="Trigger">Что запустило сверку.</param>
/// <param name="ReferencesChecked">Со сколькими эталонами фигурантов дела сверено.</param>
/// <param name="SessionsCreated">Сколько новых предложений создано (по одному на эталон с совпадениями).</param>
/// <param name="CandidatesCreated">Сколько кандидатов поставлено в очередь эксперта.</param>
/// <param name="MaxCosineDistance">Порог сверки (косинусное расстояние).</param>
/// <param name="Classification">Гриф дела.</param>
/// <param name="DivisionId">Подразделение дела.</param>
public sealed record SuggestionRunDraft(
    int AssetId,
    int CaseId,
    SuggestionTrigger Trigger,
    int ReferencesChecked,
    int SessionsCreated,
    int CandidatesCreated,
    double MaxCosineDistance,
    short Classification,
    int DivisionId);

/// <summary>Записанная сверка носителя с эталонами фигурантов дела (ТФ-ПЕР-09); поля — как у <see cref="SuggestionRunDraft"/>.</summary>
/// <param name="Id">Запись.</param>
/// <param name="AssetId">Носитель.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="Trigger">Что запустило сверку.</param>
/// <param name="ReferencesChecked">Со сколькими эталонами сверено.</param>
/// <param name="SessionsCreated">Сколько новых предложений создано.</param>
/// <param name="CandidatesCreated">Сколько кандидатов поставлено в очередь эксперта.</param>
/// <param name="MaxCosineDistance">Порог сверки.</param>
/// <param name="CreatedAtUtc">Когда сверено (UTC).</param>
public sealed record SuggestionRunRow(
    int Id,
    int AssetId,
    int CaseId,
    SuggestionTrigger Trigger,
    int ReferencesChecked,
    int SessionsCreated,
    int CandidatesCreated,
    double MaxCosineDistance,
    DateTime CreatedAtUtc);

/// <summary>
/// Кандидат «предложено системой» на лице носителя (ТФ-ПЕР-09) — для карточки носителя: где он сейчас в двойной
/// верификации (ТБ-073).
/// </summary>
/// <param name="CandidateId">Кандидат.</param>
/// <param name="SessionId">Сессия-предложение.</param>
/// <param name="CaseId">Дело.</param>
/// <param name="FaceId">Лицо носителя, похожее на эталон.</param>
/// <param name="Similarity">Сходство (1 − косинусное расстояние) — вероятностная оценка, не установление личности (ТЭ-006).</param>
/// <param name="Status">Стадия двойной верификации.</param>
/// <param name="SuggestedPersonRef">
/// Предложенный фигурант — ТОЛЬКО пока кандидат ждёт эксперта: верификатор не должен узнать предложение системы
/// из карточки носителя в обход слепой проекции (ТФ-ВЕР-02); на остальных стадиях — <c>null</c>.
/// </param>
public sealed record SuggestedCandidateRow(
    int CandidateId,
    int SessionId,
    int CaseId,
    int FaceId,
    double Similarity,
    CandidateStatus Status,
    int? SuggestedPersonRef);
