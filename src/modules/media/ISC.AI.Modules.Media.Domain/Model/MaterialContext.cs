using System;

namespace ISC.AI.Modules.Media.Domain.Model;

/// <summary>
/// Когда, откуда и где снят материал кандидата (ТФ-ПЛ-02): эксперт и верификатор видят это рядом с парой лиц.
/// Сведения о материале, а не о решениях: слепоту второй проверки (ТФ-ВЕР-02) они не затрагивают.
/// </summary>
/// <param name="CapturedAt">Время съёмки, если известно (ТФ-МЕД-17).</param>
/// <param name="UploadedAtUtc">Когда материал загружен — показывается, если время съёмки неизвестно.</param>
/// <param name="Source">Источник материала (как указали при загрузке).</param>
/// <param name="Place">Место — из привязки носителя к доступному сотруднику делу.</param>
public sealed record MaterialContext(DateTimeOffset? CapturedAt, DateTime UploadedAtUtc, string? Source, string? Place);
