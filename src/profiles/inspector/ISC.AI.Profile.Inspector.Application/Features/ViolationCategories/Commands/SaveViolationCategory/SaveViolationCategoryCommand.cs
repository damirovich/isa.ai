using ISC.AI.Abstractions.Application;
using ISC.AI.Abstractions.Audit;
using ISC.AI.Abstractions.Security;
using ISC.AI.Profile.Inspector.Domain.Services;
using Mediator;

namespace ISC.AI.Profile.Inspector.Application.Features.ViolationCategories;

/// <summary>
/// Создать или переименовать элемент классификатора видов нарушений
/// (<paramref name="CategoryId"/> = <see langword="null"/> — создание). Ведёт Администратор —
/// то же правило, что у остальных справочников (<see cref="Norms.NormGuard"/> здесь не подходит:
/// классификатор — справочник профиля, а не картотека НПА).
/// </summary>
public sealed record SaveViolationCategoryCommand(int? CategoryId, string Name, int? ParentId)
    : IRequest<ResponseDto<int>>, IAuditableRequest
{
    /// <inheritdoc />
    public AuditAction AuditAction => AuditAction.Modify;

    /// <inheritdoc />
    public string? AuditSummary =>
        $"inspector:violation-category:{(CategoryId is { } id ? id.ToString(System.Globalization.CultureInfo.InvariantCulture) : "create")}:{Name}";

    /// <inheritdoc cref="SaveViolationCategoryCommand" />
    public sealed class Handler(IViolationCategoryStore store, IUserRoleStore roles, ISubjectProvider subjectProvider)
        : IRequestHandler<SaveViolationCategoryCommand, ResponseDto<int>>
    {
        /// <summary>Единый текст отказа (правило — AdministrationRule, как у справочника подразделений).</summary>
        public const string Denied = "Классификатор видов нарушений ведёт Администратор.";

        /// <inheritdoc />
        public async ValueTask<ResponseDto<int>> Handle(
            SaveViolationCategoryCommand command, CancellationToken cancellationToken)
        {
            ArgumentNullException.ThrowIfNull(command);

            if (!await AdministrationRule.CallerCanManageAsync(roles, subjectProvider, cancellationToken))
            {
                return ResponseDto<int>.BadRequest(Denied);
            }

            if (command.CategoryId is { } categoryId)
            {
                var renamed = await store.RenameAsync(categoryId, command.Name, cancellationToken);
                return renamed switch
                {
                    ViolationWriteResult.Ok => ResponseDto<int>.Ok(categoryId),
                    ViolationWriteResult.DuplicateName =>
                        ResponseDto<int>.Conflict("Название уже занято на этом уровне."),
                    _ => ResponseDto<int>.NotFound("Элемент классификатора не найден."),
                };
            }

            var (result, createdId) = await store.CreateAsync(command.Name, command.ParentId, cancellationToken);
            return result switch
            {
                ViolationWriteResult.Ok => ResponseDto<int>.Ok(createdId),
                ViolationWriteResult.DuplicateName => ResponseDto<int>.Conflict("Название уже занято на этом уровне."),
                ViolationWriteResult.CategoryNotLeaf =>
                    ResponseDto<int>.BadRequest("Классификатор двухуровневый: вид нельзя вкладывать в вид."),
                _ => ResponseDto<int>.NotFound("Родительская сфера не найдена."),
            };
        }
    }
}
