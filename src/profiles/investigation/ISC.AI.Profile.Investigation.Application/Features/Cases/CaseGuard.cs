using ISC.AI.Abstractions.Application;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Cases;

/// <summary>Общие тексты и разбор исходов записи дел — чтобы сообщения не расходились между сценариями.</summary>
public static class CaseGuard
{
    /// <summary>Отказ по допуску при создании дела (ТБ-024: гриф/подразделение вне допуска субъекта).</summary>
    public const string OutsideClearance = "Гриф или подразделение дела вне вашего допуска (ТБ-024).";

    /// <summary>Неразличимый ответ «нет такого дела / дело недоступно» (ТБ-020/021).</summary>
    public const string NotFound = "Дело не найдено или недоступно.";

    /// <summary>Отказ по занятому номеру дела в подразделении.</summary>
    public const string DuplicateNumber = "Дело с таким номером в этом подразделении уже есть.";

    /// <summary>Единый перевод исхода записи дела в конверт ответа (для команд без полезной нагрузки).</summary>
    public static ResponseDto<bool> ToResponse(CaseWriteResult result) => result switch
    {
        CaseWriteResult.Ok => ResponseDto<bool>.Ok(true),
        CaseWriteResult.DuplicateNumber => ResponseDto<bool>.Conflict(DuplicateNumber),
        CaseWriteResult.OutsideClearance => ResponseDto<bool>.BadRequest(OutsideClearance),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}
