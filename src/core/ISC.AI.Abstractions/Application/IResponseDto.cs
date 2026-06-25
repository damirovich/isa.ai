namespace ISC.AI.Abstractions.Application;

/// <summary>
/// Негенерик-контракт конверта ответа: позволяет сквозным поведениям (обработка ошибок) формировать
/// НЕуспешный ответ, не зная типа полезной нагрузки. Реализуется <see cref="ResponseDto{T}"/>.
/// </summary>
public interface IResponseDto
{
    /// <summary>Признак успеха.</summary>
    bool Status { get; set; }

    /// <summary>Код результата.</summary>
    ResponseStatusCode StatusCode { get; set; }

    /// <summary>Человекочитаемое сообщение.</summary>
    string StatusMessage { get; set; }
}
