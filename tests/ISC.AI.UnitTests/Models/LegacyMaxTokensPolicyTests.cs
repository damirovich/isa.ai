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
            BinaryData.FromString("""{"model":"m","max_completion_tokens":4096,"messages":[]}"""), disableThinking: false);

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
                BinaryData.FromString("""{"max_completion_tokens":4096,"max_tokens":16}"""), disableThinking: false)
            .ShouldBeNull();
    }

    [Fact(DisplayName = "С флагом отключения размышлений чат-запрос получает enable_thinking=false")]
    public void Disable_thinking_flag_augments_chat_requests()
    {
        var rewritten = LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"model":"m","messages":[{"role":"user","content":"q"}]}"""),
            disableThinking: true);

        rewritten.ShouldNotBeNull();
        var json = JsonNode.Parse(rewritten)!.AsObject();
        json["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>().ShouldBeFalse();

        // Явная настройка вызывающего не перетирается; не-чат (эмбеддинги) не трогается.
        LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"messages":[],"chat_template_kwargs":{"enable_thinking":true}}"""),
            disableThinking: true).ShouldBeNull();
        LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"input":"text"}"""), disableThinking: true).ShouldBeNull();
    }

    [Fact(DisplayName = "Запросы без нового поля и не-JSON не переписываются")]
    public void Foreign_bodies_are_left_alone()
    {
        LegacyMaxTokensPolicy.RewriteBody(
            BinaryData.FromString("""{"input":"text for embeddings"}"""), disableThinking: false).ShouldBeNull();
        LegacyMaxTokensPolicy.RewriteBody(BinaryData.FromString("not json"), disableThinking: false).ShouldBeNull();
        LegacyMaxTokensPolicy.RewriteBody(BinaryData.FromString("[1,2,3]"), disableThinking: false).ShouldBeNull();
    }
}
