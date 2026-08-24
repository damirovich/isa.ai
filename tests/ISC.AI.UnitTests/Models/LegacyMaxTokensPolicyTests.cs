using System;
using System.Text.Json.Nodes;
using ISC.AI.AI.Models;
using Shouldly;
using Xunit;

namespace ISC.AI.UnitTests.Models;

/// <summary>
/// Дублирование лимита ответа старым полем <c>max_tokens</c> (инцидент 2026-08-24): llama-server
/// игнорирует новое имя <c>max_completion_tokens</c>, и без потолка «думающая» модель генерировала
/// бесконечно. Политика обязана дописывать старое поле и НЕ трогать чужие/уже корректные запросы.
/// </summary>
public sealed class LegacyMaxTokensPolicyTests
{
    [Fact(DisplayName = "max_completion_tokens дублируется в max_tokens с тем же значением")]
    public void Duplicates_new_field_into_legacy_one()
    {
        var rewritten = LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"model":"m","max_completion_tokens":4096,"messages":[]}"""));

        rewritten.ShouldNotBeNull();
        var json = JsonNode.Parse(rewritten)!.AsObject();
        json["max_tokens"]!.GetValue<int>().ShouldBe(4096);
        json["max_completion_tokens"]!.GetValue<int>().ShouldBe(4096);
        json["model"]!.GetValue<string>().ShouldBe("m"); // остальное тело не искажается
    }

    [Fact(DisplayName = "Уже заданный max_tokens не перетирается")]
    public void Existing_legacy_field_is_untouched()
    {
        LegacyMaxTokensPolicy.RewriteBody(
                BinaryData.FromString("""{"max_completion_tokens":4096,"max_tokens":16}"""))
            .ShouldBeNull();
    }

    [Fact(DisplayName = "Запросы без нового поля и не-JSON не переписываются")]
    public void Foreign_bodies_are_left_alone()
    {
        LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"input":"text for embeddings"}""")).ShouldBeNull();
        LegacyMaxTokensPolicy.RewriteBody(BinaryData.FromString("not json")).ShouldBeNull();
        LegacyMaxTokensPolicy.RewriteBody(BinaryData.FromString("[1,2,3]")).ShouldBeNull();
    }
}
