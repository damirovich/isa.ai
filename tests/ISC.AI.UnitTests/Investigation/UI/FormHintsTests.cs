using ISC.AI.Profile.Investigation.UI;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Investigation.UI;

/// <summary>
/// Подсказка у неактивной кнопки: пользователь видит, чего не хватает, а не гадает. Кнопка неактивна ровно тогда,
/// когда подсказка есть, — поэтому подсказка обязана быть пустой при заполненной форме и называть поля по порядку.
/// </summary>
public sealed class FormHintsTests
{
    [Fact(DisplayName = "Незаполненные поля перечисляются в порядке формы; всё заполнено — подсказки нет")]
    public void Missing_lists_empty_fields_in_order()
    {
        FormHints.Missing((true, "номер"), (false, "вид"), (true, "гриф")).ShouldBe("Заполните: номер, гриф");
        FormHints.Missing((false, "номер"), (false, "гриф")).ShouldBeNull();
    }

    [Fact(DisplayName = "Первая непустая подсказка из нескольких проверок")]
    public void First_returns_first_non_null()
    {
        FormHints.First(null, "Заполните: цель", "другое").ShouldBe("Заполните: цель");
        FormHints.First(null, null).ShouldBeNull();
    }

    [Fact(DisplayName = "Реквизиты задания: подсказка называет недостающие; заполнено — IsComplete")]
    public void Task_requisites_hint_matches_completeness()
    {
        var form = new TaskRequisitesForm();
        form.MissingHint.ShouldBe("Заполните: № задания, подразделение-инициатор, обоснование, цель");
        form.IsComplete.ShouldBeFalse();

        form.TaskNumber = "3-17/26";
        form.InitiatorUnitId = 4;
        form.Justification = "информация";
        form.MissingHint.ShouldBe("Заполните: цель");

        form.Purpose = "установить связи";
        form.MissingHint.ShouldBeNull();
        form.IsComplete.ShouldBeTrue();
    }
}
