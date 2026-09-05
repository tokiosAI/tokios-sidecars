using System.Text.Json;
using Tokios.ChatGPT.Sidecar;

namespace Tokios.ChatGPT.Sidecar.Tests;

public sealed class FlattenerTests
{
    private static FlattenedRequest Flatten(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return ChatRequestFlattener.Flatten(doc.RootElement);
    }

    private static ChatRequestException Reject(string json) =>
        Assert.Throws<ChatRequestException>(() => Flatten(json));

    [Theory]
    [InlineData("tools")]
    [InlineData("tool_choice")]
    [InlineData("functions")]
    [InlineData("function_call")]
    public void Rejects_ClientToolCalling(string field)
    {
        var ex = Reject($$"""{"model":"chatgpt-sidecar","messages":[{"role":"user","content":"hi"}],"{{field}}":[]}""");
        Assert.Contains($"'{field}' is not supported", ex.Message);
    }

    [Fact]
    public void Rejects_ToolRoleMessages()
    {
        var ex = Reject("""{"messages":[{"role":"user","content":"hi"},{"role":"tool","content":"result"}]}""");
        Assert.Contains("tool messages are not supported", ex.Message);
    }

    [Fact]
    public void Rejects_EmptyMessages() =>
        Assert.Contains("non-empty array", Reject("""{"messages":[]}""").Message);

    [Fact]
    public void Rejects_NonTextContentParts() =>
        Assert.Contains("text parts only", Reject(
            """{"messages":[{"role":"user","content":[{"type":"image_url","image_url":{"url":"x"}}]}]}""").Message);

    [Fact]
    public void SingleUserTurn_PassesThroughUntouched()
    {
        var req = Flatten("""{"messages":[{"role":"user","content":"What is 2+2?"}]}""");
        Assert.Equal("What is 2+2?", req.Prompt);
        Assert.Null(req.SystemPrompt);
        Assert.False(req.Stream);
        Assert.False(req.IncludeUsage);
        Assert.Null(req.Effort);
    }

    [Fact]
    public void SystemAndDeveloperMessages_ConcatenateIntoSystemPrompt()
    {
        var req = Flatten("""
            {"messages":[
                {"role":"system","content":"Be terse."},
                {"role":"developer","content":"Answer in JSON."},
                {"role":"user","content":"hi"}
            ]}
            """);
        Assert.Equal("Be terse.\n\nAnswer in JSON.", req.SystemPrompt);
        Assert.Equal("hi", req.Prompt);
    }

    [Fact]
    public void MultiTurn_RendersAsLabeledTranscript()
    {
        var req = Flatten("""
            {"messages":[
                {"role":"user","content":"pick a color"},
                {"role":"assistant","content":"blue"},
                {"role":"user","content":"why?"}
            ]}
            """);
        Assert.Equal("User:\npick a color\n\nAssistant:\nblue\n\nUser:\nwhy?", req.Prompt);
    }

    [Theory]
    [InlineData("minimal", "low")]
    [InlineData("low", "low")]
    [InlineData("medium", "medium")]
    [InlineData("high", "high")]
    [InlineData("xhigh", "xhigh")]
    public void ReasoningEffort_MapsToCodexLevels(string openAi, string codex)
    {
        var req = Flatten($$"""{"messages":[{"role":"user","content":"hi"}],"reasoning_effort":"{{openAi}}"}""");
        Assert.Equal(codex, req.Effort);
    }

    [Theory]
    [InlineData("max")]
    [InlineData("turbo")]
    public void ReasoningEffort_RejectsUnknownLevels(string effort)
    {
        var ex = Reject($$"""{"messages":[{"role":"user","content":"hi"}],"reasoning_effort":"{{effort}}"}""");
        Assert.Contains($"Unsupported reasoning_effort '{effort}'", ex.Message);
    }

    [Fact]
    public void IncludeUsage_OnlyWhenStreamingAndRequested()
    {
        Assert.True(Flatten("""{"messages":[{"role":"user","content":"hi"}],"stream":true,"stream_options":{"include_usage":true}}""").IncludeUsage);
        Assert.False(Flatten("""{"messages":[{"role":"user","content":"hi"}],"stream":true}""").IncludeUsage);
        Assert.False(Flatten("""{"messages":[{"role":"user","content":"hi"}],"stream_options":{"include_usage":true}}""").IncludeUsage);
    }

    [Fact]
    public void Model_CarriedThroughWhenPresent()
    {
        Assert.Equal("gpt-5.6-terra", Flatten(
            """{"model":"gpt-5.6-terra","messages":[{"role":"user","content":"hi"}]}""").Model);
        Assert.Null(Flatten("""{"messages":[{"role":"user","content":"hi"}]}""").Model);
    }

    [Fact]
    public void Rejects_NonStringModel() =>
        Assert.Contains("'model' must be a string", Reject(
            """{"model":5,"messages":[{"role":"user","content":"hi"}]}""").Message);

    [Fact]
    public void ContentPartArrays_ConcatenateTextParts()
    {
        var req = Flatten("""{"messages":[{"role":"user","content":[{"type":"text","text":"a"},{"type":"text","text":"b"}]}]}""");
        Assert.Equal("ab", req.Prompt);
    }
}
