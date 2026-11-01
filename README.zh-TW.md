# PamisWang.CodexAgentSdk

[English](README.md) | 繁體中文

OpenAI Codex 的 .NET 用戶端：啟動本機的 `codex app-server`，用 JSON-RPC 建立 thread、執行 turn、串流事件、處理核准請求。

> **非官方**。移植自 [openai/codex 的 Python SDK](https://github.com/openai/codex/tree/main/sdk/python)（Apache-2.0），與 OpenAI 無關。
> 協定型別由 `codex app-server generate-json-schema` 產生的 schema 自動產生。

## 狀態

預覽版已經發佈到 NuGet；1.0 之前 API 可能還會變。

```bash
dotnet add package PamisWang.CodexAgentSdk --prerelease
```

| 對照 | 版本 |
|---|---|
| Python SDK | openai/codex `67f0994` |
| 協定 schema | Codex CLI 0.166.0 |
| 最低 CLI 版本（較新的選項） | 0.151.0 |

## 跟官方 SDK 比較

- 官方 TypeScript SDK 包的是 `codex exec`（每輪一個程序）；Python SDK 與這個套件包的是 `codex app-server`。Python 的公開 API 全部移植，對應見下方的[對照表](#跟-python-sdk-的對應)。
- 比官方多：公開的 `ServerRequestHandler` 與 `ApprovalMode.User`（Python 的核准 handler 只在內部），handler 在背景執行；協定的字串列舉可擴充（例如 `TurnStatus`），聯集遇到不認得的變體變成 `Unknown…`，新版 Codex 不會讓解析失敗；`WarningCallback`；取消 `RunAsync` 時會中斷這一輪。
- 沒有的：同步 API、只給內部使用的 goal 操作、`launch_args_override`；也不附帶 Codex runtime（Python 版相依 `openai-codex-cli-bin`），要自己安裝 Codex CLI。
- 命名跟 [PamisWang.ClaudeAgentSdk](https://github.com/pamis-wang/claude-agent-sdk-dotnet) 一致（`CodexAgentClient`、`CodexAgentOptions`、`ConnectAsync`、`CliPath`、`WarningCallback`、原始 JSON 一律叫 `Raw`），不照 Python 的名稱。
- 完整對照：[與官方 Python、TypeScript SDK](docs/comparison-with-official-sdks.zh-TW.md)、[與 PamisWang.ClaudeAgentSdk](docs/claude-vs-codex-dotnet.zh-TW.md)

## 使用方式

```csharp
using PamisWang.CodexAgentSdk;
using PamisWang.CodexAgentSdk.Protocol;

await using var codex = new CodexAgentClient();
var thread = await codex.StartThreadAsync(new ThreadStartOptions
{
    WorkingDirectory = @"D:\work\my-project",
    Sandbox = Sandbox.WorkspaceWrite,
});

// 等整輪完成
var result = await thread.RunAsync("用三點說明這個專案。");
Console.WriteLine(result.FinalResponse);

// 逐段串流
using var turn = await thread.StartTurnAsync("那測試怎麼跑？", new TurnOptions { Effort = "high" });
await foreach (var notification in turn.StreamAsync())
{
    switch (notification.Payload)
    {
        case AgentMessageDeltaNotification delta:
            Console.Write(delta.Delta);
            break;
        case ItemCompletedNotification { Item: CommandExecutionThreadItem command }:
            Console.WriteLine($"\n[{command.Status}] {command.Command}");
            break;
        case TurnCompletedNotification completed:
            Console.WriteLine($"\n{completed.Turn.Status}");
            break;
    }
}

// 之後接續同一段對話
var resumed = await codex.ResumeThreadAsync(thread.Id);
```

- 預設沿用 `codex login` 存下的 ChatGPT 登入狀態，不需要 API Key；也可以用 `LoginChatgptAsync`、`LoginChatgptDeviceCodeAsync`、`LoginApiKeyAsync` 登入。
- 第一次呼叫 API 時才啟動 app-server；要提早啟動就呼叫 `ConnectAsync`。
- 通知的 `Payload` 是產生的協定型別；不認得的通知是 `UnknownNotification`，原始 JSON 在 `Raw`，不會被丟掉。
- 取消 `CodexThread.RunAsync` 的 `CancellationToken` 會先中斷這一輪；`TurnHandle` 上的取消只停止讀取，要中斷請呼叫 `InterruptAsync`。

### 核准

```csharp
await using var codex = new CodexAgentClient(new CodexAgentOptions
{
    ServerRequestHandler = async (request, cancellationToken) => request.Params switch
    {
        CommandExecutionRequestApprovalParams command => new CommandExecutionRequestApprovalResponse
        {
            Decision = await AskUserAsync(command.Command, cancellationToken)
                ? CommandExecutionApprovalDecision.Accept
                : CommandExecutionApprovalDecision.Decline,
        },
        FileChangeRequestApprovalParams => new FileChangeRequestApprovalResponse { Decision = FileChangeApprovalDecision.Decline },
        _ => null,
    },
});

var thread = await codex.StartThreadAsync(new ThreadStartOptions
{
    Sandbox = Sandbox.ReadOnly,
    ApprovalMode = ApprovalMode.User, // 核准請求交給上面的 handler
});
```

- `ApprovalMode.AutoReview`（預設，跟 Python SDK 一樣）先由 Codex 自動審查；`ApprovalMode.User` 是這個套件另外加的，把核准請求交給 handler；`ApprovalMode.DenyAll` 一律不核准。
- 沒給 handler 時跟 Python SDK 一樣：指令與檔案變更的核准一律 `accept`。
- handler 在背景執行，不會擋住事件串流；請求被撤回（`serverRequest/resolved`）時 `cancellationToken` 會被取消。

## 跟 Python SDK 的對應

| Python | .NET |
|---|---|
| `AsyncCodex` / `Codex` | `CodexAgentClient`（只有非同步） |
| `CodexConfig` | `CodexAgentOptions` |
| `codex.thread_start(...)` | `StartThreadAsync(ThreadStartOptions)` |
| `codex.thread_resume` / `thread_fork` / `thread_list` / `thread_archive` / `thread_unarchive` | `ResumeThreadAsync` / `ForkThreadAsync` / `ListThreadsAsync` / `ArchiveThreadAsync` / `UnarchiveThreadAsync` |
| `codex.models()` / `account()` / `logout()` | `ListModelsAsync` / `GetAccountAsync` / `LogoutAsync` |
| `codex.login_chatgpt()` / `login_chatgpt_device_code()` / `login_api_key()` | `LoginChatgptAsync` / `LoginChatgptDeviceCodeAsync` / `LoginApiKeyAsync` |
| `thread.run(...)` / `thread.turn(...)` | `RunAsync(RunInput, TurnOptions)` / `StartTurnAsync(RunInput, TurnOptions)` |
| `thread.read()` / `set_name()` / `compact()` | `ReadAsync` / `SetNameAsync` / `CompactAsync` |
| `TurnHandle.stream()` / `run()` / `steer()` / `interrupt()` | `StreamAsync` / `RunAsync` / `SteerAsync` / `InterruptAsync` |
| `TextInput`、`ImageInput`、`LocalImageInput`、`SkillInput`、`MentionInput`、`ExternalMessage` | 同名 record |
| `Sandbox`、`ApprovalMode` | 同名 enum（多了 `ApprovalMode.User`） |
| `CodexError`、`JsonRpcError`、`ServerBusyError`… | `CodexAgentException`、`JsonRpcException`、`ServerBusyException`… |
| `retry_on_overload` / `is_retryable_error` | `CodexRetry.RetryOnOverloadAsync` / `CodexRetry.IsRetryableError` |
| `openai_codex.types` | `PamisWang.CodexAgentSdk.Protocol` |

## 需求

- .NET 10
- Codex CLI。Windows 請用原生的 `codex.exe`（官方 PowerShell 安裝程式會裝在 `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`），不支援 npm 的 `codex.cmd`（批次檔會讓 cmd.exe 重新解析參數，有指令注入風險）。
- CLI 最低版本 0.151.0。指定 `WarningCallback` 時，連線時用 initialize 回報的版本檢查，太舊就透過它警告，但不會中斷連線；環境變數 `CODEX_AGENT_SDK_SKIP_VERSION_CHECK` 設成任何值就不檢查。
- `TurnOptions.Source`、`TurnOptions.TurnServiceTier`、`ExternalMessage` 與 `IncludeTurns` 需要 CLI 0.151.0 以上；版本不夠時送出前就丟 `CodexAgentException`，不會被舊版默默忽略。

## 更新協定型別

```bash
python tools/generate_protocol.py --from-codex <path-to-codex>   # 用指定的 CLI 更新 schema/ 後重新產生
python tools/generate_protocol.py                                 # 只用已提交的 schema 重新產生
```

只產生 SDK 用得到的型別：所有 server 通知與 server 請求，以及 `CLIENT_METHODS` 列出的 client 方法。

## 測試

```bash
dotnet test
```

單元測試用記憶體裡的假 app-server。實際啟動 Codex、會消耗額度的整合測試要設環境變數 `CODEX_INTEGRATION_TESTS=1` 才會執行。

## 授權

Apache-2.0，見 [LICENSE](LICENSE)；移植來源的聲明見 [NOTICE](NOTICE)。
