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

    /// <summary>
    /// Внешняя зависимость временно недоступна — управляемая деградация, не ошибка приложения
    /// (ТН-003, ТНД-001): напр. сервер инференса не отвечает. Отличается от <see cref="InternalServerError"/>
    /// тем, что это ожидаемое временное состояние, а не дефект — UI показывает «повторите позже».
    /// </summary>
    ServiceUnavailable = 6,
}
