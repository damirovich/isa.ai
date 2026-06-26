using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Ingestion;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Loading;

using ResModel = ResponseDto<IngestFileResult>;

/// <summary>
/// Загрузка одного файла в корпус (Э4-01): содержимое + объявленные гриф/подразделение → извлечение
/// текста → <see cref="IFileIngestor"/> (fail-closed гриф, дедуп). Гриф ДЕКЛАРИРУЕТСЯ оператором.
/// </summary>
/// <param name="Content">Байты файла.</param>
/// <param name="FileName">Имя файла (по нему выбирается извлекатель).</param>
/// <param name="DocType">Тип документа.</param>
/// <param name="Classification">Гриф (объявляется оператором; для открытых данных — 0).</param>
/// <param name="DivisionId">Подразделение.</param>
public sealed record IngestFileCommand(
    byte[] Content, string FileName, string DocType, short Classification, int DivisionId) : IRequest<ResModel>
{
    /// <summary>Обработчик: оборачивает байты в поток и передаёт в файловый загрузчик корпуса.</summary>
    public sealed class Handler(IFileIngestor fileIngestor) : IRequestHandler<IngestFileCommand, ResModel>
    {
        /// <inheritdoc />
        public async ValueTask<ResModel> Handle(IngestFileCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            using var stream = new MemoryStream(command.Content);
            var result = await fileIngestor.IngestFileAsync(
                new FileIngestionRequest(stream, command.FileName, command.DocType, command.Classification, command.DivisionId),
                cancellationToken);

            var payload = new IngestFileResult(
                FileName: command.FileName,
                Accepted: result.Accepted,
                DocumentId: result.DocumentId,
                ChunkCount: result.ChunkCount,
                Reason: result.RejectionReason,
                IsDuplicate: result.Accepted && result.ChunkCount == 0);

            return ResModel.Ok(payload);
        }
    }
}
