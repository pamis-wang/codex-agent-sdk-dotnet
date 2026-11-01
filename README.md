# PamisWang.CodexAgentSdk

English | [繁體中文](https://github.com/pamis-wang/codex-agent-sdk-dotnet/blob/main/README.zh-TW.md)

A .NET client for OpenAI Codex: it launches the local `codex app-server` and talks to it over JSON-RPC to start threads, run turns, stream events, and answer approval requests.

> **Unofficial.** Ported from the [Python SDK in openai/codex](https://github.com/openai/codex/tree/main/sdk/python) (Apache-2.0) and not affiliated with OpenAI.
> Protocol types are generated from the schema emitted by `codex app-server generate-json-schema`.

## Status

Preview; the API may change before 1.0.

| Tracks | Version |
|---|---|
| Python SDK | openai/codex `67f0994` |
| Protocol schema | Codex CLI 0.166.0 |
| Minimum CLI for newer options | 0.151.0 |

## Compared with the official SDKs

- The official TypeScript SDK wraps `codex exec` (one process per turn); the Python SDK and this package wrap `codex app-server`. Python's whole public API is ported; see the [mapping table](#mapping-to-the-python-sdk) below.
- Beyond the official SDKs: a public `ServerRequestHandler` and `ApprovalMode.User` (Python keeps its approval handler internal), with the handler running in the background; extensible protocol string enums (such as `TurnStatus`) and `Unknown…` union variants, so newer Codex versions don't break parsing; `WarningCallback`; and cancelling `RunAsync` interrupts the turn.
- Not included: the sync API, the internal-only goal operations, and `launch_args_override`; no Codex runtime is bundled either (the Python SDK depends on `openai-codex-cli-bin`), so install the Codex CLI yourself.
- Names follow [PamisWang.ClaudeAgentSdk](https://github.com/pamis-wang/claude-agent-sdk-dotnet) (`CodexAgentClient`, `CodexAgentOptions`, `ConnectAsync`, `CliPath`, `WarningCallback`, raw JSON always named `Raw`) rather than the Python names.
- Full comparisons: [with the official Python and TypeScript SDKs](https://github.com/pamis-wang/codex-agent-sdk-dotnet/blob/main/docs/comparison-with-official-sdks.md), [with PamisWang.ClaudeAgentSdk](https://github.com/pamis-wang/codex-agent-sdk-dotnet/blob/main/docs/claude-vs-codex-dotnet.md)

## Usage

```csharp
using PamisWang.CodexAgentSdk;
using PamisWang.CodexAgentSdk.Protocol;

await using var codex = new CodexAgentClient();
var thread = await codex.StartThreadAsync(new ThreadStartOptions
{
    WorkingDirectory = @"D:\work\my-project",
    Sandbox = Sandbox.WorkspaceWrite,
});

// wait for the whole turn
var result = await thread.RunAsync("Describe this project in three bullets.");
Console.WriteLine(result.FinalResponse);

// stream as it happens
using var turn = await thread.StartTurnAsync("How do I run the tests?", new TurnOptions { Effort = "high" });
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

// continue the same conversation later
var resumed = await codex.ResumeThreadAsync(thread.Id);
```

- The ChatGPT sign-in saved by `codex login` is reused by default, so no API key is needed; `LoginChatgptAsync`, `LoginChatgptDeviceCodeAsync`, and `LoginApiKeyAsync` are also available.
- The app-server starts on the first API call; call `ConnectAsync` to start it earlier.
- A notification's `Payload` is a generated protocol type; unrecognized notifications arrive as `UnknownNotification` with the raw JSON in `Raw` instead of being dropped.
- Cancelling `CodexThread.RunAsync` also interrupts the turn; cancelling on a `TurnHandle` only stops reading, so call `InterruptAsync` to stop it.

### Approvals

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
    ApprovalMode = ApprovalMode.User, // send approval requests to the handler above
});
```

- `ApprovalMode.AutoReview` (the default, as in the Python SDK) lets Codex review requests automatically; `ApprovalMode.User`, added by this package, sends them to the handler; `ApprovalMode.DenyAll` never approves.
- Without a handler, command and file-change approvals are accepted, as in the Python SDK.
- The handler runs in the background without blocking the event stream; its `cancellationToken` is cancelled when the request is withdrawn (`serverRequest/resolved`).

## Mapping to the Python SDK

| Python | .NET |
|---|---|
| `AsyncCodex` / `Codex` | `CodexAgentClient` (async only) |
| `CodexConfig` | `CodexAgentOptions` |
| `codex.thread_start(...)` | `StartThreadAsync(ThreadStartOptions)` |
| `codex.thread_resume` / `thread_fork` / `thread_list` / `thread_archive` / `thread_unarchive` | `ResumeThreadAsync` / `ForkThreadAsync` / `ListThreadsAsync` / `ArchiveThreadAsync` / `UnarchiveThreadAsync` |
| `codex.models()` / `account()` / `logout()` | `ListModelsAsync` / `GetAccountAsync` / `LogoutAsync` |
| `codex.login_chatgpt()` / `login_chatgpt_device_code()` / `login_api_key()` | `LoginChatgptAsync` / `LoginChatgptDeviceCodeAsync` / `LoginApiKeyAsync` |
| `thread.run(...)` / `thread.turn(...)` | `RunAsync(RunInput, TurnOptions)` / `StartTurnAsync(RunInput, TurnOptions)` |
| `thread.read()` / `set_name()` / `compact()` | `ReadAsync` / `SetNameAsync` / `CompactAsync` |
| `TurnHandle.stream()` / `run()` / `steer()` / `interrupt()` | `StreamAsync` / `RunAsync` / `SteerAsync` / `InterruptAsync` |
| `TextInput`, `ImageInput`, `LocalImageInput`, `SkillInput`, `MentionInput`, `ExternalMessage` | records with the same names |
| `Sandbox`, `ApprovalMode` | enums with the same names (plus `ApprovalMode.User`) |
| `CodexError`, `JsonRpcError`, `ServerBusyError`… | `CodexAgentException`, `JsonRpcException`, `ServerBusyException`… |
| `retry_on_overload` / `is_retryable_error` | `CodexRetry.RetryOnOverloadAsync` / `CodexRetry.IsRetryableError` |
| `openai_codex.types` | `PamisWang.CodexAgentSdk.Protocol` |

## Requirements

- .NET 10
- The Codex CLI. On Windows, use the native `codex.exe` (the official PowerShell installer puts it in `%LOCALAPPDATA%\Programs\OpenAI\Codex\bin`); the npm `codex.cmd` wrapper is not supported, because cmd.exe re-parses the arguments of batch files, which allows command injection.
- The minimum CLI version is 0.151.0. With `WarningCallback` set, the version reported by initialize is checked on connect and a warning is sent through it when the CLI is older, without failing the connection; set `CODEX_AGENT_SDK_SKIP_VERSION_CHECK` to any value to skip the check.
- `TurnOptions.Source`, `TurnOptions.TurnServiceTier`, `ExternalMessage`, and `IncludeTurns` need CLI 0.151.0 or newer; older versions get a `CodexAgentException` before sending instead of silently ignoring them.

## Updating protocol types

```bash
python tools/generate_protocol.py --from-codex <path-to-codex>   # refresh schema/ from that CLI, then regenerate
python tools/generate_protocol.py                                 # regenerate from the committed schema
```

Only the types the SDK needs are generated: every server notification and server request, plus the client methods listed in `CLIENT_METHODS`.

## Tests

```bash
dotnet test
```

Unit tests use an in-memory fake app-server. Integration tests that launch Codex and consume quota run only when `CODEX_INTEGRATION_TESTS=1` is set.

## License

Apache-2.0, see [LICENSE](https://github.com/pamis-wang/codex-agent-sdk-dotnet/blob/main/LICENSE). The notices of the ported source are in [NOTICE](https://github.com/pamis-wang/codex-agent-sdk-dotnet/blob/main/NOTICE).
