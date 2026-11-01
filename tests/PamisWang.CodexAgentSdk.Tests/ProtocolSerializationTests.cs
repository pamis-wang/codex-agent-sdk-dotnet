using System.Text.Json;
using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk.Tests;

/// <summary>產生的協定型別：聯集、字串列舉與未知值的處理。</summary>
public class ProtocolSerializationTests
{
    [Fact]
    public void ThreadItem_KnownType_DeserializesToVariant()
    {
        var item = JsonSerializer.Deserialize(
            """{"type": "agentMessage", "id": "m1", "text": "hi", "phase": "final_answer", "startedAtMs": 5}""",
            ProtocolJsonContext.Default.ThreadItem);

        var message = Assert.IsType<AgentMessageThreadItem>(item);
        Assert.Equal("hi", message.Text);
        Assert.Equal(MessagePhase.FinalAnswer, message.Phase);
        Assert.Equal(5, message.StartedAtMs);
        Assert.Equal("agentMessage", message.Type);
    }

    [Fact]
    public void ThreadItem_UnknownType_KeepsRawJson()
    {
        var item = JsonSerializer.Deserialize(
            """{"type": "brandNewItem", "id": "x", "payload": [1, 2]}""",
            ProtocolJsonContext.Default.ThreadItem);

        var unknown = Assert.IsType<UnknownThreadItem>(item);
        Assert.Equal("brandNewItem", unknown.Type);
        Assert.Equal(2, unknown.Raw.GetProperty("payload").GetArrayLength());
    }

    [Fact]
    public void ThreadItem_KnownTypeMissingRequiredField_FallsBackToUnknown()
    {
        // 協定改版拿掉了必填欄位：不能讓整則通知解析失敗
        var item = JsonSerializer.Deserialize("""{"type": "agentMessage", "id": "m1"}""", ProtocolJsonContext.Default.ThreadItem);

        Assert.IsType<UnknownThreadItem>(item);
    }

    [Fact]
    public void UserInput_SerializesDiscriminatorFirst()
    {
        IReadOnlyList<UserInput> input = [new TextUserInput { Text = "你好" }, new LocalImageUserInput { Path = "a.png" }];

        var json = JsonSerializer.Serialize(input, ProtocolJsonContext.Default.IReadOnlyListUserInput);

        // 非 ASCII 字元會跳脫成 \uXXXX，跟 Python 的 json.dumps 預設一樣，仍是合法的 JSON
        Assert.StartsWith("""[{"type":"text","text":""", json, StringComparison.Ordinal);
        Assert.EndsWith("""{"type":"localImage","path":"a.png"}]""", json, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(json);
        Assert.Equal("你好", document.RootElement[0].GetProperty("text").GetString());
    }

    [Fact]
    public void StringValue_UnknownValue_IsPreserved()
    {
        var status = JsonSerializer.Deserialize("\"paused\"", ProtocolJsonContext.Default.TurnStatus);

        Assert.Equal("paused", status.Value);
        Assert.NotEqual(TurnStatus.Completed, status);
        Assert.Equal("\"paused\"", JsonSerializer.Serialize(status, ProtocolJsonContext.Default.TurnStatus));
    }

    [Fact]
    public void JsonUnion_StringMember_RoundTripsAndCompares()
    {
        var json = JsonSerializer.Serialize(AskForApproval.OnRequest, ProtocolJsonContext.Default.AskForApproval);
        var parsed = JsonSerializer.Deserialize(json, ProtocolJsonContext.Default.AskForApproval);

        Assert.Equal("\"on-request\"", json);
        Assert.Equal(AskForApproval.OnRequest, parsed);
        Assert.NotEqual(AskForApproval.Never, parsed);
    }

    [Fact]
    public void JsonUnion_ObjectVariant_CanBeBuiltAndRead()
    {
        var granular = new GranularAskForApproval
        {
            Granular = new GranularAskForApprovalGranular { McpElicitations = true, Rules = false, SandboxApproval = true },
        };

        var value = AskForApproval.From(granular);

        Assert.Null(value.StringValue);
        Assert.True(value.AsGranularAskForApproval()?.Granular.SandboxApproval);
        Assert.Null(AskForApproval.Never.AsGranularAskForApproval());
    }

    [Fact]
    public void ApprovalResponse_SerializesDecision()
    {
        var response = new CommandExecutionRequestApprovalResponse { Decision = CommandExecutionApprovalDecision.Decline };

        var json = JsonSerializer.Serialize(response, ProtocolJsonContext.Default.CommandExecutionRequestApprovalResponse);

        Assert.Equal("""{"decision":"decline"}""", json);
    }

    [Fact]
    public void Params_NullOptionalFields_AreOmitted()
    {
        var json = JsonSerializer.Serialize(new ThreadReadParams { ThreadId = "t1" }, ProtocolJsonContext.Default.ThreadReadParams);

        Assert.Equal("""{"threadId":"t1"}""", json);
    }

    [Fact]
    public void Registry_MapsNotificationToTurnAndThread()
    {
        using var document = JsonDocument.Parse(Json.Delta("thr_1", "turn_1", "hi"));

        var payload = ProtocolRegistry.DeserializeNotification(ServerNotificationMethods.ItemAgentMessageDelta, document.RootElement);

        Assert.IsType<AgentMessageDeltaNotification>(payload);
        Assert.Equal("turn_1", ProtocolRegistry.NotificationTurnId(payload!));
        Assert.Equal("thr_1", ProtocolRegistry.NotificationThreadId(payload!));
    }

    [Fact]
    public void Registry_NestedTurnId_IsUsed()
    {
        using var document = JsonDocument.Parse(Json.TurnEvent("thr_1", "turn_9", "completed"));

        var payload = ProtocolRegistry.DeserializeNotification(ServerNotificationMethods.TurnCompleted, document.RootElement);

        Assert.Equal("turn_9", ProtocolRegistry.NotificationTurnId(payload!));
    }
}
