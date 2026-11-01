using PamisWang.CodexAgentSdk.Protocol;

namespace PamisWang.CodexAgentSdk;

/// <summary>
/// 把公開的選項轉成協定參數。對應 Python SDK 的 <c>_sandbox.py</c>、<c>_approval_mode.py</c>、<c>_inputs.py</c>。
/// </summary>
internal static class ProtocolMapping
{
    /// <summary>thread 生命週期（start、resume、fork）用的 sandbox 模式。</summary>
    public static SandboxMode? ToSandboxMode(Sandbox? sandbox) => sandbox switch
    {
        null => null,
        Sandbox.ReadOnly => SandboxMode.ReadOnly,
        Sandbox.WorkspaceWrite => SandboxMode.WorkspaceWrite,
        Sandbox.FullAccess => SandboxMode.DangerFullAccess,
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "不認得的 Sandbox。"),
    };

    /// <summary>turn 用的 sandbox 政策。</summary>
    public static SandboxPolicy? ToSandboxPolicy(Sandbox? sandbox) => sandbox switch
    {
        null => null,
        Sandbox.ReadOnly => new ReadOnlySandboxPolicy(),
        Sandbox.WorkspaceWrite => new WorkspaceWriteSandboxPolicy(),
        Sandbox.FullAccess => new DangerFullAccessSandboxPolicy(),
        _ => throw new ArgumentOutOfRangeException(nameof(sandbox), sandbox, "不認得的 Sandbox。"),
    };

    /// <summary>
    /// 核准模式對應的核准政策與審查者：<see cref="ApprovalMode.AutoReview"/> 是 <c>on-request</c> 加自動審查，
    /// <see cref="ApprovalMode.DenyAll"/> 是 <c>never</c>，<see cref="ApprovalMode.User"/> 是 <c>on-request</c> 交給 client。
    /// </summary>
    public static (AskForApproval? Policy, ApprovalsReviewer? Reviewer) ToApprovalSettings(ApprovalMode? mode) => mode switch
    {
        null => (null, null),
        ApprovalMode.AutoReview => (AskForApproval.OnRequest, ApprovalsReviewer.AutoReview),
        ApprovalMode.DenyAll => (AskForApproval.Never, null),
        ApprovalMode.User => (AskForApproval.OnRequest, ApprovalsReviewer.User),
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "不認得的 ApprovalMode。"),
    };

    /// <summary>一般輸入轉成協定的 <see cref="UserInput"/>。</summary>
    public static List<UserInput> ToUserInputs(IEnumerable<InputItem> items) =>
        [.. items.Select(item => item switch
        {
            TextInput text => (UserInput)new TextUserInput { Text = text.Text },
            ImageInput image => new ImageUserInput { Url = image.Url },
            LocalImageInput local => new LocalImageUserInput { Path = local.Path },
            SkillInput skill => new SkillUserInput { Name = skill.Name, Path = skill.Path },
            MentionInput mention => new MentionUserInput { Name = mention.Name, Path = mention.Path },
            _ => throw new ArgumentException($"不支援的輸入：{item.GetType()}", nameof(items)),
        })];

    /// <summary>
    /// turn 的輸入：一般輸入放 <c>input</c>；外部訊息放 <c>toolOutput</c>，<c>input</c> 留空。
    /// </summary>
    public static (List<UserInput> Input, TurnToolOutput? ToolOutput) ToTurnInput(RunInput input)
    {
        ArgumentNullException.ThrowIfNull(input);
        if (input.ExternalMessage is { } message)
        {
            if (string.IsNullOrWhiteSpace(message.ToolName))
            {
                throw new ArgumentException("ExternalMessage.ToolName 不能是空字串。", nameof(input));
            }

            return ([], new TurnToolOutput { Name = message.ToolName, Namespace = message.Namespace, Output = message.Content });
        }

        return (ToUserInputs(input.Items!), null);
    }
}
