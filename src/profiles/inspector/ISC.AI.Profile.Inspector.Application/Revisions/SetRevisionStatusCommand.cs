using ISC.AI.Abstractions.Application;
using ISC.AI.Profile.Inspector.Domain.Enums;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Revisions;

using ResModel = ResponseDto<int>;

/// <summary>
/// Сменить статус редакции НПА (Э4-02, ТФ-НПА-02): доменный статус + материализация видимости связанных
/// чанков в ядре (утратившая силу перестаёт выдаваться как действующая, GATE-3).
/// </summary>
/// <param name="NormRevisionId">Идентификатор редакции.</param>
/// <param name="Status">Новый статус (действующая/утратила силу).</param>
public sealed record SetRevisionStatusCommand(int NormRevisionId, RevisionStatus Status) : IRequest<ResModel>
{
    /// <summary>Обработчик: делегирует доменной службе материализации (ResponseDto с числом затронутых чанков).</summary>
    public sealed class Handler(IRevisionStatusMaterializer materializer) : IRequestHandler<SetRevisionStatusCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(SetRevisionStatusCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            var affectedChunks = await materializer.SetStatusAsync(command.NormRevisionId, command.Status, cancellationToken);
            return ResModel.Ok(affectedChunks, $"Статус редакции обновлён; затронуто чанков: {affectedChunks}.");
        }
    }
}
