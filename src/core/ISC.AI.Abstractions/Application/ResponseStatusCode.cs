namespace ISC.AI.Abstractions.Application;

/// <summary>Код результата сценария (use-case) для единого конверта ответов <see cref="ResponseDto{T}"/>.</summary>
public enum ResponseStatusCode
{
    /// <summary>Успех.</summary>
    Ok = 0,

    /// <summary>Некорректный запрос (валидация входа).</summary>
    BadRequest = 1,

    /// <summary>Ресурс не найден.</summary>
    NotFound = 2,

    /// <summary>Ошибка валидации бизнес-правил.</summary>
    ValidationError = 3,

    /// <summary>Конфликт состояния (ресурс уже существует и т. п.).</summary>
    Conflict = 4,

    /// <summary>Внутренняя ошибка.</summary>
    InternalServerError = 5,
}
