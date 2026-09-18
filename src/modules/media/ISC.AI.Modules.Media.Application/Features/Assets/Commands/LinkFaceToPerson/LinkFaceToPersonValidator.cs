using FluentValidation;

namespace ISC.AI.Modules.Media.Application.Features.Assets;

/// <summary>Правила ручной привязки лица к фигуранту (ТФ-МЕД-03, ТБ-071): лицо, основание дела, фигурант.</summary>
public sealed class LinkFaceToPersonValidator : AbstractValidator<LinkFaceToPersonCommand>
{
    /// <inheritdoc cref="LinkFaceToPersonValidator" />
    public LinkFaceToPersonValidator()
    {
        RuleFor(c => c.FaceId).GreaterThan(0);
        RuleFor(c => c.AuthorizationId).GreaterThan(0).WithMessage("Привязка возможна только по основанию дела (ТБ-071).");
        RuleFor(c => c.PersonRef).GreaterThan(0).WithMessage("Укажите фигуранта дела (ТФ-МЕД-03).");
    }
}
