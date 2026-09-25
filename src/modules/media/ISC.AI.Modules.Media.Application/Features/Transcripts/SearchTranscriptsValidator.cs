using FluentValidation;

namespace ISC.AI.Modules.Media.Application.Features.Transcripts;

/// <summary>
/// Правила поиска по расшифровкам (ADR-0026): дело указано; искомый текст — от
/// <see cref="SearchTranscriptsQuery.MinTextLength"/> до <see cref="SearchTranscriptsQuery.MaxTextLength"/>
/// символов ПОСЛЕ обрезки пробелов (один символ совпадает почти со всем — такая выдача бессмысленна).
/// </summary>
public sealed class SearchTranscriptsValidator : AbstractValidator<SearchTranscriptsQuery>
{
    /// <inheritdoc cref="SearchTranscriptsValidator" />
    public SearchTranscriptsValidator()
    {
        RuleFor(q => q.CaseId).GreaterThan(0);
        RuleFor(q => q.Text)
            .Must(text => text is not null
                && text.Trim().Length >= SearchTranscriptsQuery.MinTextLength
                && text.Trim().Length <= SearchTranscriptsQuery.MaxTextLength)
            .WithMessage(
                $"Искомый текст — от {SearchTranscriptsQuery.MinTextLength} до {SearchTranscriptsQuery.MaxTextLength} символов.");
    }
}
