using ISC.AI.Modules.DocFlow.Application.Features.DocumentTypes;
using ISC.AI.Modules.DocFlow.Domain.Enums;
using Shouldly;

namespace ISC.AI.UnitTests.DocFlow;

/// <summary>
/// Валидаторы справочника типов документов (Э4-35 этап 1, ТЗ СКИД §3.1): правила формы без БД —
/// уникальность имени проверяется хранилищем и интеграционным тестом.
/// </summary>
public sealed class DocumentTypeValidatorTests
{
    private readonly CreateDocumentTypeValidator _create = new();
    private readonly UpdateDocumentTypeValidator _update = new();
    private readonly ChangeDocumentTypeGroupValidator _changeGroup = new();

    [Fact(DisplayName = "Создание: пустое имя отклоняется")]
    public void Create_rejects_empty_name() =>
        _create.Validate(new CreateDocumentTypeCommand("", DocumentGroup.Execution)).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: имя длиннее 200 символов отклоняется")]
    public void Create_rejects_overlong_name() =>
        _create.Validate(new CreateDocumentTypeCommand(new string('а', 201), DocumentGroup.Storage))
            .IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: неизвестная группа отклоняется")]
    public void Create_rejects_unknown_group() =>
        _create.Validate(new CreateDocumentTypeCommand("Поручение", (DocumentGroup)42)).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Создание: корректная команда проходит")]
    public void Create_accepts_valid_command() =>
        _create.Validate(new CreateDocumentTypeCommand("Поручение", DocumentGroup.Execution))
            .IsValid.ShouldBeTrue();

    [Fact(DisplayName = "Правка: неположительный идентификатор отклоняется")]
    public void Update_rejects_non_positive_id() =>
        _update.Validate(new UpdateDocumentTypeCommand(0, "Справка", true)).IsValid.ShouldBeFalse();

    [Fact(DisplayName = "Смена группы: неизвестная группа отклоняется")]
    public void ChangeGroup_rejects_unknown_group() =>
        _changeGroup.Validate(new ChangeDocumentTypeGroupCommand(1, (DocumentGroup)0)).IsValid.ShouldBeFalse();
}
