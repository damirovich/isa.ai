using System.Diagnostics.CodeAnalysis;

namespace ISC.AI.Abstractions.Application;

/// <summary>
/// Единый конверт ответа сценария (use-case) для команд/запросов Mediator: полезная нагрузка плюс
/// статус, код и сообщение. Нейтрален к домену — общий контракт прикладного слоя платформы.
/// </summary>
/// <typeparam name="T">Тип полезной нагрузки.</typeparam>
[SuppressMessage("Design", "CA1000:Do not declare static members on generic types",
    Justification = "Фабричные методы — намеренный паттерн конверта результата (ResponseDto<T>.Ok/Fail/NotFound/...).")]
public sealed class ResponseDto<T> : IResponseDto
{
    /// <summary>Полезная нагрузка (при успехе).</summary>
    public T? Data { get; set; }

    /// <inheritdoc />
    public bool Status { get; set; }

    /// <inheritdoc />
    public ResponseStatusCode StatusCode { get; set; }

    /// <inheritdoc />
    public string StatusMessage { get; set; } = string.Empty;

    /// <summary>Общее число элементов (для постраничных выборок), если применимо.</summary>
    public int? TotalCount { get; set; }

    /// <summary>Успех с полезной нагрузкой.</summary>
    public static ResponseDto<T> Ok(T data, string message = "Успешно") =>
        new() { Data = data, Status = true, StatusCode = ResponseStatusCode.Ok, StatusMessage = message };

    /// <summary>Успех с полезной нагрузкой и общим числом элементов.</summary>
    public static ResponseDto<T> Ok(T data, int? totalCount, string message = "Успешно") =>
        new() { Data = data, Status = true, StatusCode = ResponseStatusCode.Ok, StatusMessage = message, TotalCount = totalCount };

    /// <summary>Синоним <see cref="Ok(T, string)"/>.</summary>
    public static ResponseDto<T> Success(T data, string message = "Успешно") => Ok(data, message);

    /// <summary>Ресурс не найден.</summary>
    public static ResponseDto<T> NotFound(string message = "Запрашиваемый ресурс не найден") =>
        new() { Status = false, StatusCode = ResponseStatusCode.NotFound, StatusMessage = message };

    /// <summary>Некорректный запрос.</summary>
    public static ResponseDto<T> BadRequest(string message = "Некорректный запрос") =>
        new() { Status = false, StatusCode = ResponseStatusCode.BadRequest, StatusMessage = message };

    /// <summary>Ошибка (по умолчанию — валидация бизнес-правил).</summary>
    public static ResponseDto<T> Fail(string message, ResponseStatusCode code = ResponseStatusCode.ValidationError) =>
        new() { Status = false, StatusCode = code, StatusMessage = message };

    /// <summary>Конфликт состояния.</summary>
    public static ResponseDto<T> Conflict(string message = "Конфликт: ресурс уже существует.") =>
        new() { Status = false, StatusCode = ResponseStatusCode.Conflict, StatusMessage = message };

    /// <summary>Внутренняя ошибка.</summary>
    public static ResponseDto<T> InternalServerError(string message = "Внутренняя ошибка") =>
        new() { Status = false, StatusCode = ResponseStatusCode.InternalServerError, StatusMessage = message };
}
