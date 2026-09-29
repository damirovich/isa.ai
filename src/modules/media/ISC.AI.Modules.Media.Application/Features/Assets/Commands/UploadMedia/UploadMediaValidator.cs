using FluentValidation;
using ISC.AI.Modules.Media.Domain.Model;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Правила загрузки носителя (ТС-010, ТФ-МЕД-01; аудио — ADR-0026): дело, имя, allowlist формата, размер, длины реквизитов.</summary>
/// <remarks>Пределы длин — публичные константы: интерфейс берёт <c>MaxLength</c> отсюда, а не дублирует числа.</remarks>
public sealed class UploadMediaValidator : AbstractValidator<UploadMediaCommand>
{
    /// <summary>Предел длины поля «Источник/происхождение».</summary>
    public const int MaxSourceLength = 1000;

    /// <summary>Предел длины поля «Место» (привязка носителя к делу).</summary>
    public const int MaxPlaceLength = 500;

    /// <inheritdoc cref="UploadMediaValidator" />
    public UploadMediaValidator()
    {
        RuleFor(c => c.CaseId).GreaterThan(0);
        RuleFor(c => c.FileName).NotEmpty().MaximumLength(MediaFileRules.MaxFileNameLength);
        RuleFor(c => c.ContentType).NotEmpty().MaximumLength(200)
            .Must(MediaFileRules.IsAllowed)
            .WithMessage("Допустимые форматы носителя: JPEG, PNG, BMP, WebP, MP4, WebM, MKV, MOV, AVI; "
                + "аудио — MP3, M4A, AAC, OGG/Opus, WAV, WebM, FLAC, AMR, 3GP.");
        RuleFor(c => c.Content)
            .Must(content => content is { LongLength: > 0 and <= MediaFileRules.MaxFileBytes })
            .WithMessage("Файл пуст или больше 200 МБ.");
        // ТФ-МЕД-17: у фото и видео дата и время съёмки ОБЯЗАТЕЛЬНЫ (оператор подтверждает значение из метаданных
        // или вводит вручную); у аудио — по желанию. Дата из будущего — ошибка ввода (сутки — запас на пояса).
        RuleFor(c => c.CapturedAt)
            .NotNull()
            .When(c => MediaFileRules.KindOf(c.ContentType) is MediaKind.Image or MediaKind.Video)
            .WithMessage("Укажите дату и время съёмки: для фото и видео они обязательны (ТФ-МЕД-17).");
        RuleFor(c => c.CapturedAt)
            .Must(value => value is null || value.Value <= DateTimeOffset.UtcNow.AddDays(1))
            .WithMessage("Дата съёмки не может быть в будущем.");
        RuleFor(c => c.Source).MaximumLength(MaxSourceLength);
        RuleFor(c => c.Place).MaximumLength(MaxPlaceLength);
    }
}
