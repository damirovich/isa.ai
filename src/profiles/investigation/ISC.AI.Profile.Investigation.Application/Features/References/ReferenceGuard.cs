using ISC.AI.Abstractions.Application;
using ISC.AI.Profile.Investigation.Domain.Services;

namespace ISC.AI.Profile.Investigation.Application.Features.References;

/// <summary>Общие тексты и разбор исходов записи справочников профиля (ТФ-АДМ-07).</summary>
public static class ReferenceGuard
{
    /// <summary>Запись справочника не найдена.</summary>
    public const string NotFound = "Запись справочника не найдена.";

    /// <summary>Наименование занято в этом справочнике.</summary>
    public const string Duplicate = "Запись с таким наименованием в этом справочнике уже есть.";

    /// <summary>Предел длины наименования (тот же, что у таблицы).</summary>
    public const int MaxNameLength = 300;

    /// <summary>Предел длины кода (тот же, что у таблицы).</summary>
    public const int MaxCodeLength = 50;

    /// <summary>
    /// Наименование для сводки аудита (ТБ-030): обрезано по краям и по пределу длины, переводы строк и
    /// управляющие символы заменены пробелом. Сводка пишется ДО валидации команды (поведение аудита стоит
    /// снаружи проверки), поэтому сюда может прийти и отклонённое наименование — оно не должно раздувать
    /// журнал или изображать в нём строку другого события.
    /// </summary>
    public static string AuditName(string? name)
    {
        var text = new string((name ?? string.Empty).Select(c => char.IsControl(c) ? ' ' : c).ToArray()).Trim();
        return text.Length <= MaxNameLength ? text : text[..MaxNameLength];
    }

    /// <summary>Перевод исхода записи в конверт ответа для команд без полезной нагрузки.</summary>
    public static ResponseDto<bool> ToResponse(ReferenceWriteResult result) => result switch
    {
        ReferenceWriteResult.Ok => ResponseDto<bool>.Ok(true),
        ReferenceWriteResult.Duplicate => ResponseDto<bool>.Conflict(Duplicate),
        _ => ResponseDto<bool>.NotFound(NotFound),
    };
}
