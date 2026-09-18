using ISC.AI.Abstractions.Application;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.Persons;

/// <summary>Общие тексты и разбор исходов записи фигурантов (ТФ-ПЕР-01).</summary>
public static class PersonGuard
{
    /// <summary>Неразличимый ответ «нет такого фигуранта/дела либо недоступны» (ТБ-020/021).</summary>
    public const string NotFound = "Фигурант или дело не найдены либо недоступны.";

    /// <summary>
    /// Неразличимый отказ добавления эталона (ТБ-020/021): фигурант недоступен, носитель не из дела фигуранта
    /// или вне допуска, лицо не с этого носителя — один и тот же ответ.
    /// </summary>
    public const string ReferenceNotFound = "Фигурант, носитель или лицо не найдены либо недоступны.";

    /// <summary>Перевод исхода записи в конверт ответа для команд без полезной нагрузки.</summary>
    public static ResponseDto<bool> ToResponse(PersonWriteResult result) => result switch
    {
        PersonWriteResult.Ok => ResponseDto<bool>.Ok(true),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}
