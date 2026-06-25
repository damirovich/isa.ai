using ISC.AI.Profile.Inspector.Application.Generation;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles;

/// <summary>Валидатор команды генерации справки (запускается сквозным ValidationBehavior, Э4-03).</summary>
public sealed class GenerateReferenceValidatorTests
{
    [Theory(DisplayName = "Валидатор: пустая/слишком короткая тема — невалидна")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("ab")]
    public void Invalid_topics_are_rejected(string topic)
    {
        var result = new GenerateReferenceValidator().Validate(new GenerateReferenceCommand(topic));

        result.IsValid.ShouldBeFalse();
    }

    [Fact(DisplayName = "Валидатор: осмысленная тема — валидна")]
    public void Valid_topic_passes()
    {
        var result = new GenerateReferenceValidator().Validate(new GenerateReferenceCommand("режим хранения ДСП"));

        result.IsValid.ShouldBeTrue();
    }
}
