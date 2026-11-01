# codex-agent-sdk-dotnet compared with the official Python and TypeScript SDKs

English | [繁體中文](comparison-with-official-sdks.zh-TW.md)

How `PamisWang.CodexAgentSdk` compares with OpenAI's official Codex SDKs for Python and TypeScript. The .NET package is ported from the Python SDK, and both use `codex app-server`; the TypeScript SDK uses `codex exec`.

- Surveyed: 2026-11-01
- Official Python: openai-codex (openai/codex main 67f0994)
- Official TypeScript: @openai/codex-sdk 0.166.0
- .NET: PamisWang.CodexAgentSdk 0.1.0-preview.1
- Protocol schema: Codex CLI 0.166.0
- openai-codex on PyPI is 0.163.0; the .NET package is ported from the Python source on the repo's main branch. Verified against Codex CLI 0.166.0 on Windows with a ChatGPT sign-in.

Related: [Claude .NET vs Codex .NET](claude-vs-codex-dotnet.md). The comparison of Claude .NET with the official SDKs is in `docs/comparison-with-official-sdks.md` of the claude-agent-sdk-dotnet repo.

## Key points

1. **The official SDKs use different transports**: TypeScript wraps `codex exec --experimental-json` (one process per turn, one-way events); Python and .NET wrap `codex app-server` (long-running, JSON-RPC), so .NET follows Python feature for feature.
2. **Python's whole public API is ported**: Threads (start, resume, fork, list, archive, read, rename, compact), turns (run, stream, steer, interrupt), sign-in (browser, device code, API key), model listing, and retries. Not ported: the sync API, the internal-only goal operations, `launch_args_override`, and the bundled runtime.
3. **Approvals go to the caller**: Python's approval handler is internal and accepts everything by default; .NET exposes `ServerRequestHandler` and adds `ApprovalMode.User` to send approval requests to it. The handler runs in the background without blocking the event stream, and its token is cancelled when the request is withdrawn.
4. **Holds up when the protocol changes**: In Python, a new enum value or union variant fails validation and the whole notification becomes `UnknownNotification`; in .NET, string enums keep new values and unions fall back to `Unknown…` variants while the other fields still parse.
5. **Generated protocol types**: Python generates every v2 type with datamodel-code-generator (about 14,000 lines); .NET uses its own generator for only the 461 types the SDK needs (about 10,800 lines), with a source-generated context for AOT compatibility.
6. **Names follow Claude .NET**: `CodexAgentClient`, `CodexAgentOptions`, `ConnectAsync`, `CliPath`, `WarningCallback`, and raw JSON always named `Raw`; the Python names are given in comments and the README's mapping table.

## Feature matrix

Legend: ✅ Supported, ⭐ Extra, 🟡 Partial, ❌ No, — N/A (⭐ marks what goes beyond the official SDKs)

### Connection and process

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Transport** | ✅ `codex app-server --listen stdio://` | 🟡 `codex exec --experimental-json`, one-way JSONL events | ✅ `codex app-server --listen stdio://` |
| **Process lifetime** | ✅ `Codex()` / `AsyncCodex()`, long-running | 🟡 one process per `run()` | ✅ long-running; starts on the first call or `ConnectAsync`, closed by `DisposeAsync` |
| **Connection state** | 🟡 `metadata` | — | ✅ `IsConnected`, `Metadata`, `ServerInfo` |
| **Executable** | ✅ `codex_bin`; defaults to the bundled `openai-codex-cli-bin` | ✅ `codexPathOverride`; defaults to `@openai/codex` | 🟡 `CliPath`, PATH, and the official install location; no bundled runtime, refuses `.cmd` on Windows |
| **Custom launch command** | ✅ `launch_args_override` | ❌ | ❌ |
| **`--config` overrides** | ✅ `config_overrides` | ✅ `config`, `configOverrides` | ✅ `ConfigOverrides` |
| **Environment variables** | ✅ `env` | ✅ `env` | ✅ `EnvironmentVariables` |
| **stderr** | 🟡 drained in the background, last 400 lines kept | 🟡 attached to the error on failure | ⭐ `StandardErrorCallback`, with the last 400 lines added to exceptions |
| **Sync API** | ✅ `Codex` | ❌ | ❌ async only |
| **Concurrent turn streams** | ✅ routed by turn ID | 🟡 one process each | ✅ routed by turn ID |

### Authentication and account

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Reuse `codex login`** | ✅ | ✅ | ✅ |
| **ChatGPT browser sign-in** | ✅ `login_chatgpt()` | ❌ | ✅ `LoginChatgptAsync` |
| **Device code sign-in** | ✅ `login_chatgpt_device_code()` | ❌ | ✅ `LoginChatgptDeviceCodeAsync` |
| **API key** | ✅ `login_api_key()` | ✅ `apiKey` option | ✅ `LoginApiKeyAsync` |
| **Account / sign-out** | ✅ `account()`, `logout()` | ❌ | ✅ `GetAccountAsync`, `LogoutAsync` |

### Threads

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Start / resume** | ✅ `thread_start()`, `thread_resume()` | ✅ `startThread()`, `resumeThread()` | ✅ `StartThreadAsync`, `ResumeThreadAsync` |
| **Fork / list / archive / unarchive** | ✅ `thread_fork`, `thread_list`, `thread_archive`, `thread_unarchive` | ❌ | ✅ `ForkThreadAsync`, `ListThreadsAsync`, `ArchiveThreadAsync`, `UnarchiveThreadAsync` |
| **Read / rename / compact** | ✅ `read()`, `set_name()`, `compact()` | ❌ | ✅ `ReadAsync`, `SetNameAsync`, `CompactAsync` |
| **Instructions** | ✅ `base_instructions`, `developer_instructions` | ❌ | ✅ `BaseInstructions`, `DeveloperInstructions` |
| **Ephemeral threads** | ✅ `ephemeral` | ❌ | ✅ `Ephemeral` |

### Turns

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Run and collect the result** | ✅ `run()` → `TurnResult` | ✅ `run()` → `finalResponse`, `items`, `usage` | ✅ `RunAsync` → `TurnResult` |
| **Turn handle** | ✅ `turn()` → `TurnHandle` | 🟡 `runStreamed()` | ✅ `StartTurnAsync` → `TurnHandle` |
| **Interrupt** | ✅ `interrupt()` | 🟡 `AbortSignal`, which kills the process | ⭐ `InterruptAsync`; cancelling `RunAsync` also interrupts the turn |
| **Steer** | ✅ `steer()` | ❌ | ✅ `SteerAsync` |
| **Input kinds** | ✅ text, image, local image, skill, mention, `ExternalMessage` | 🟡 text, local image | ✅ same as Python |
| **Structured output** | ✅ `output_schema` | ✅ `outputSchema` | ✅ `OutputSchema` |
| **Model / effort / summary** | ✅ per turn | 🟡 per thread only | ✅ `TurnOptions` |
| **Sandbox** | ✅ `Sandbox` | ✅ `sandboxMode` | ✅ `Sandbox` |
| **Service tier / source label** | ✅ `service_tier`, `turn_service_tier`, `source` | ❌ | ✅ `ServiceTier`, `TurnServiceTier`, `Source` |
| **Final response** | ✅ the `final_answer` message, else the last unlabelled one | ✅ `finalResponse` | ✅ same as Python |

### Event streaming

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Streaming API** | ✅ `stream()` | ✅ async generator | ✅ `IAsyncEnumerable<Notification>` |
| **Text deltas** | ✅ `item/agentMessage/delta` | ❌ only item started, updated, and completed | ✅ `AgentMessageDeltaNotification` |
| **Typed events** | ✅ every server notification is typed | 🟡 8 exec event types | ✅ 85 notifications (CLI 0.166.0), all typed |
| **Unrecognized notifications** | ✅ `UnknownNotification` (`.params`) | ❌ | ✅ `UnknownNotification` (`.Raw`) |
| **Raw JSON kept** | 🟡 only for unrecognized notifications | ❌ | ⭐ `Raw` on every notification |
| **New enum values** | ❌ validation fails and the whole notification becomes unknown | — | ⭐ the new value is kept and compares as usual |
| **New union variants** | ❌ validation fails | — | ⭐ falls back to an `Unknown…` variant with the raw JSON |
| **Events before the turn/start response** | ✅ buffered (`pending_turn`) | — | ✅ buffered |

### Approvals and interaction

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Approval modes** | 🟡 `auto_review`, `deny_all` | 🟡 `approvalPolicy` | ⭐ `AutoReview`, `DenyAll`, `User` |
| **Custom approval handler** | ❌ internal only, accepts everything by default | ❌ | ⭐ public `ServerRequestHandler` |
| **Without a handler** | ✅ command and file-change approvals are accepted | — | ✅ same as Python |
| **Events while the handler runs** | ❌ the read loop stalls | — | ⭐ runs in the background; events keep arriving |
| **Handler throws** | ❌ the read loop ends and the connection is lost | — | ⭐ answered with a JSON-RPC error; the connection is unaffected |
| **Request withdrawn** | ❌ | — | ⭐ `serverRequest/resolved` cancels the handler's token |

### Versions, errors, and retries

| Feature | Official Python (openai-codex) | Official TypeScript (@openai/codex-sdk) | .NET (PamisWang.CodexAgentSdk) |
|---|---|---|---|
| **Minimum version** | ✅ 0.151.0, checked before sending newer fields | ❌ | ⭐ same as Python, plus `WarningCallback` on connect |
| **Unversioned dev builds** | ✅ checks fields by generating the schema | — | ✅ same as Python |
| **Exception types** | ✅ `JsonRpcError` and subclasses | 🟡 plain `Error` | ✅ `JsonRpcException` and subclasses |
| **Overload retries** | ✅ `retry_on_overload` | ❌ | ⭐ `CodexRetry`, which also recognizes v2's `serverOverloaded` |
| **Failed turns** | 🟡 `RuntimeError` | ✅ `turn.failed` event | ⭐ `TurnFailedException` with the whole `Turn` |

## Size and quality

| Item | Official Python | Official TypeScript | .NET |
|---|---|---|---|
| Hand-written code | about 4,200 lines | about 1,070 lines | about 3,870 lines of C# (36 files) |
| Generated protocol types | about 14,200 lines (every v2 type) | hand-written `events.ts`, `items.ts` | about 10,800 lines (461 types) |
| Tests | app-server harness and real integration tests | fake Responses API proxy | 77 unit tests (fake app-server) + 3 real integration tests |
| Verified with a real CLI | — | — | Codex CLI 0.166.0: streaming, `RunAsync`, and the approval flow (declining a file write) all pass |
| License | Apache-2.0 | Apache-2.0 | Apache-2.0, with the ported source noted in NOTICE |
| AOT | — | — | `IsAotCompatible`, zero build warnings |

## Current limitations

- No Codex runtime is bundled; install the Codex CLI (0.151.0 or newer) yourself, and use the native `codex.exe` on Windows.
- Async API only; there is no equivalent of Python's sync `Codex`.
- No public API for general notifications: notifications that belong to no turn (such as `account/updated`) are kept only for the latest 1,000 and cannot be read yet.
- No public low-level request API, so most of the roughly 180 app-server methods that aren't wrapped can't be called yet.
- `thread/start.dynamicTools` (custom tools inside the SDK) is experimental and will be considered once the protocol settles.

