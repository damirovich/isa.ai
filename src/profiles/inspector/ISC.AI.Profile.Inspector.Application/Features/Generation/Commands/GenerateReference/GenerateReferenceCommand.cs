using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Enums;
using ISC.AI.Abstractions.Grounding;
using ISC.AI.Abstractions.Rag;
using ISC.AI.Abstractions.Security;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.Generation;

using ResModel  = ResponseDto<GenerateReferenceResult>;
/// <summary>
/// Команда генерации информационной справки по теме на основе извлечённых НПА (ТФ-ГЕН-01).
/// Результат — ЧЕРНОВИК, требующий проверки человеком (HITL, ТБ-042), в конверте <see cref="ResponseDto{T}"/>.
/// </summary>
/// <param name="Topic">Тема/запрос инспектора (используется для семантического извлечения норм).</param>
public sealed record GenerateReferenceCommand(string Topic) : IRequest<ResModel>, IGroundedScenario
{
    // Аудит генерации (AuditAction.Generate) пишет RAG-оркестратор ядра (GroundedGenerator) с ТОЧНЫМ грифом
    // (=max грифов фрагментов), id фрагментов и payload запрос/ответ — поэтому команда НЕ помечается
    // IAuditableRequest (иначе двойная запись одного события в неизменяемый журнал).

    /// <summary>
    /// Обработчик сценария: контекст доступа → задачный промпт (Scriban) → RAG-оркестратор ядра
    /// (извлечение с фильтром доступа → модель → грунтовка → аудит) → черновик с пометкой HITL.
    /// Ссылки формируются ТОЛЬКО из извлечённых фрагментов — грунтовку гарантирует ядро (ТБ-040/041).
    /// </summary>
    public sealed class Handler(IGroundedGenerator generator, IAccessContextProvider accessContextProvider, IReferencePromptRenderer promptRenderer)
        : IRequestHandler<GenerateReferenceCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(GenerateReferenceCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            // Валидация входа — в сквозном ValidationBehavior (GenerateReferenceValidator), не здесь.
            // Контекст доступа субъекта (dev-заглушка до Э3-08; затем внешний SSO). Fail-closed — в ядре.
            var access = await accessContextProvider.GetCurrentAsync(cancellationToken);

            var response = await generator.GenerateAsync(
                // Роль Draft — быстрая (без размышлений, ADR-0011): черновик по шаблону, думать не над чем.
                new GroundedRequest(command.Topic, ModelRole.Draft, TaskPrompt: promptRenderer.Render(command)),
                access,
                cancellationToken);

            // «Думающая» модель может потратить весь лимит вывода на размышления и вернуть ПУСТОЙ текст —
            // это сбой, а не черновик: честный отказ (инцидент 2026-08-24, как в Редакторе).
            if (string.IsNullOrWhiteSpace(response.Answer))
            {
                return ResponseDto<GenerateReferenceResult>.BadRequest(
                    "Модель вернула пустой ответ (весь лимит вывода ушёл на размышления). "
                    + "Повторите попытку; если повторяется — увеличьте Llm:Generation:MaxOutputTokens.");
            }

            var result = new GenerateReferenceResult(
                DraftText: response.Answer,
                RequiresHumanReview: true, // HITL: результат всегда проект, требующий проверки (ТБ-042/ТЭ-002).
                AllCitationsConfirmed: response.Grounding.AllConfirmed,
                Citations: response.Grounding.Citations,
                ResultClassification: response.ResultClassification);

            return ResModel.Ok(result);
        }
    }
}
