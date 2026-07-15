using ISC.AI.Profile.Inspector.Application.Grounding;
using Shouldly;

namespace ISC.AI.UnitTests.Profiles.Grounding;

/// <summary>Профильная канонизация НПА (Э4-18, §5.3.1.2): склонения/сокращения к канон-форме, симметрично.</summary>
public sealed class NpaCitationNormalizerTests
{
    private readonly NpaCitationNormalizer _normalizer = new();

    [Fact(DisplayName = "Нормализатор НПА: «ст. 12»/«статьи 12»/«Статья 12» → «статья 12»")]
    public void Article_forms_canonicalize_equally()
    {
        _normalizer.Normalize("ст. 12").ShouldContain("статья 12");
        _normalizer.Normalize("статьи 12").ShouldContain("статья 12");
        _normalizer.Normalize("Статья 12").ShouldContain("статья 12");
    }

    [Fact(DisplayName = "Нормализатор НПА: «№ 45» → «№45» (склейка номера)")]
    public void Act_number_glued()
    {
        _normalizer.Normalize("Закон № 45").ShouldContain("№45");
    }

    [Fact(DisplayName = "Нормализатор НПА: склонения кодекса/Конституции к общей форме")]
    public void Code_and_constitution_canonicalize()
    {
        _normalizer.Normalize("Уголовного кодекса").ShouldBe(_normalizer.Normalize("уголовный кодекс"));
        _normalizer.Normalize("Конституции").ShouldContain("конституция");
    }

    [Fact(DisplayName = "Нормализатор НПА: пустой вход — пустая строка")]
    public void Empty_input()
    {
        _normalizer.Normalize("   ").ShouldBeEmpty();
    }
}
