using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Application.Features.Generation;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;
using Scriban;

namespace ISC.AI.Profile.Inspector.Application.Features.Meetings;

/// <summary>
/// Справка об исполнении поручений протокола (ТФ-СОВ-02, §5.2.7): состояние пунктов считает КОД
/// из документооборота (<see cref="IMeetingProtocolReader"/> — от имени субъекта, решётка там),
/// ИИ быстрой ролью Draft пишет только связующий текст с эскалацией по просроченным.
/// Результат — проект для человека (ТБ-042); конверт общий с Генератором.
/// </summary>
/// <param name="DocumentId">Id протокола (документа документооборота).</param>
public sealed record GenerateMeetingReportCommand(int DocumentId)
    : IRequest<ResponseDto<GenerateReferenceResult>>, IGroundedScenario
{
    // НАМЕРЕННО не IAuditableRequest: аудит генерации пишет оркестратор ядра (иначе двойная запись).

    /// <inheritdoc cref="GenerateMeetingReportCommand" />
    public sealed class Handler(
        IGroundedGenerator generator,
        IMeetingProtocolReader protocolReader,
        IAccessContextProvider accessContextProvider,
        Abstractions.AI.IPromptProvider promptProvider)
        : IRequestHandler<GenerateMeetingReportCommand, ResponseDto<GenerateReferenceResult>>
    {
        private readonly Template _template =
            Template.Parse(promptProvider.GetTaskPrompt("meeting-report").Text);

        /// <inheritdoc />
        public async ValueTask<ResponseDto<GenerateReferenceResult>> Handle(
            GenerateMeetingReportCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Недоступный по решётке протокол неотличим от несуществующего (ТБ-020-стиль).
            var protocol = await protocolReader.ReadAsync(command.DocumentId, cancellationToken);
            if (protocol is null)
            {
                return ResponseDto<GenerateReferenceResult>.NotFound("Протокол не найден.");
            }

            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);
            var facts = MeetingReportFacts.Build(protocol);
            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): факты готовы, думать не над чем.
                new GroundedRequest(
                    "исполнение поручений, контроль сроков",
                    ModelRole.Draft,
                    TaskPrompt: _template.Render(new { facts })),
                access,
                cancellationToken);

            if (string.IsNullOrWhiteSpace(response.Answer))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest(
                    "Модель вернула пустой ответ. Повторите попытку; если повторяется — "
                    + "увеличьте Llm:Generation:MaxOutputTokens.");
            }

            // Гриф справки — не ниже грифа протокола (наследование, ТБ-033): генератор наследует
            // гриф фрагментов НПА, но факты пришли из РЕЖИМНОГО документа — берём максимум.
            var classification = (short)Math.Max(response.ResultClassification, protocol.Classification);

            return ResponseDto<GenerateReferenceResult>.Ok(new GenerateReferenceResult(
                DraftText: response.Answer,
                RequiresHumanReview: true, // ТБ-042: справку подписывает человек.
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: classification));
        }
    }
}
