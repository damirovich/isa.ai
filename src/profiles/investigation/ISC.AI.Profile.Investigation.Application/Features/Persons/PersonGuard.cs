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

    /// <summary>Отказ по связи (ТФ-ПЕР-06): связь с фигурантом другого дела или с самим собой, неверный тип связи.</summary>
    public const string InvalidLink =
        "Связь не согласована: выберите фигуранта этого дела и действующий тип связи из справочника (ТФ-ПЕР-06).";

    /// <summary>Перевод исхода записи в конверт ответа для команд без полезной нагрузки.</summary>
    public static ResponseDto<bool> ToResponse(PersonWriteResult result) => result switch
    {
        PersonWriteResult.Ok => ResponseDto<bool>.Ok(true),
        PersonWriteResult.InvalidLink => ResponseDto<bool>.BadRequest(InvalidLink),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}
