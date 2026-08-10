using FluentValidation;
using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Modules.DocFlow.Application.Features.Notifications;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using ISC.AI.Modules.DocFlow.Domain.Services;
using Mediator;

namespace ISC.AI.Modules.DocFlow.Application.Features.Comments;

/// <summary>Единое сопоставление статуса записи комментария с ответом (тексты — в одном месте).</summary>
internal static class CommentResponses
{
    public static ResponseDto<bool> ToResponse(CommentWriteStatus status) => status switch
    {
        CommentWriteStatus.Ok => ResponseDto<bool>.Ok(true),
        CommentWriteStatus.NotFound => ResponseDto<bool>.NotFound("Комментарий не найден."),
        CommentWriteStatus.NotAuthor => ResponseDto<bool>.BadRequest("Изменять и удалять можно только свой комментарий."),
        CommentWriteStatus.AlreadyDeleted => ResponseDto<bool>.BadRequest("Комментарий удалён — правка невозможна."),
        CommentWriteStatus.OnlyRootCanBeResolved =>
            ResponseDto<bool>.BadRequest("Закрыть можно только обсуждение целиком, не отдельный ответ (ТЗ §4.8)."),
        CommentWriteStatus.Conflict =>
            ResponseDto<bool>.BadRequest("Комментарий изменён параллельно — обновите страницу и повторите."),
        _ => ResponseDto<bool>.Fail("Не удалось выполнить действие с комментарием."),
    };
}
