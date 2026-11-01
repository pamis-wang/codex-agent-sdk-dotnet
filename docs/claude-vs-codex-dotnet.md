# claude-agent-sdk-dotnet compared with codex-agent-sdk-dotnet

English | [繁體中文](claude-vs-codex-dotnet.zh-TW.md)

Both SDKs port an official Python SDK to .NET and talk to a local CLI through a subprocess, but their protocols, conversation models, and sources of types differ. This report compares their architecture, APIs, and naming, and lists what they could share or borrow from each other.

- Surveyed: 2026-11-01
- Claude: PamisWang.ClaudeAgentSdk 0.1.0-preview.5
- Codex: PamisWang.CodexAgentSdk 0.1.0-preview.1

Related: [Codex .NET vs official SDKs](comparison-with-official-sdks.md). The [comparison of Claude .NET with the official SDKs](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/docs/comparison-with-official-sdks.md) is in the claude-agent-sdk-dotnet repo.

## Key points

1. **The same skeleton**: Both target .NET 10 with no dependencies, are AOT-compatible, build with `TreatWarningsAsErrors`, and share the same `.editorconfig` and build settings; `SubprocessTransport`, `NdjsonReader`, and `CliLocator` (refusing Windows batch files) are nearly identical.
2. **The protocol differs most**: Claude uses stream-json plus a control protocol, with one conversation per client; Codex uses JSON-RPC (without the `jsonrpc` field), and one client can run many threads and turns at once.
3. **Different sources of types**: Claude has no official schema, so its message types are hand-written and tested against fixtures recorded from the real CLI; Codex has `generate-json-schema`, and its 461 protocol types are generated.
4. **Names are aligned**: The client, options, base exception, `ConnectAsync`, `CliPath`, `WarningCallback`, `StandardErrorCallback`, `MaxBufferSize`, and the `Raw` JSON property match; the deliberate differences all come from the protocols.
5. **Each test strategy has strengths**: Claude has 190 tests and recorded fixtures, which guard well against regressions; Codex has a fake app-server plus optional integration tests that exercise the real CLI.
6. **Room to borrow**: Codex could add protocol fixtures recorded from the real CLI; Claude could adopt Codex's tolerance for unknown values and its background permission handler.

## Basics

| Item | Claude .NET | Codex .NET |
|---|---|---|
| Package | `PamisWang.ClaudeAgentSdk` 0.1.0-preview.5 | `PamisWang.CodexAgentSdk` 0.1.0-preview.1 |
| Ported from | claude-agent-sdk-python v0.2.174 (MIT) | openai/codex `sdk/python` @ 67f0994 (Apache-2.0) |
| Own license | MIT | Apache-2.0 |
| CLI | Claude Code CLI 2.1.312, minimum 2.0.0 | Codex CLI 0.166.0, minimum 0.151.0 |
| Target / dependencies | .NET 10, no package dependencies | .NET 10, no package dependencies |
| AOT | `IsAotCompatible` | `IsAotCompatible` (protocol types ship with a source-generated context) |
| Hand-written code | about 2,560 lines (48 files) | about 3,870 lines (36 files) |
| Generated code | none | about 10,800 lines (`tools/generate_protocol.py`) |
| Tests | 190; recorded fixtures + fake CLI subprocess | 77 unit + 3 real integration (needs `CODEX_INTEGRATION_TESTS=1`) |

## Architecture

| Aspect | Claude .NET | Codex .NET |
|---|---|---|
| Command | `claude --output-format stream-json --verbose … --input-format stream-json` | `codex app-server --listen stdio://` |
| Message format | one JSON object per line: user and assistant messages, control requests and responses | one JSON-RPC 2.0-style message per line, without the `jsonrpc` field |
| Conversation model | one session per client, messages read in order | many threads per client, many turns per thread, running concurrently |
| Request matching | the control request's `request_id` | JSON-RPC `id` (a GUID string) |
| Event dispatch | a single reader hands messages to the caller in order | `MessageRouter` routes by turn ID; each subscriber has its own cursor |
| Permissions / approvals | the CLI sends a `can_use_tool` control request, handled by `CanUseTool` | the server sends a JSON-RPC request, handled by `ServerRequestHandler` (in the background) |
| Initialization | control `initialize`, returning `ServerInfo` (raw JSON) | `initialize` → `initialized`, returning `InitializeResponse` and `CodexServerInfo` |
| Version check | runs `claude -v` before connecting | reads the `userAgent` reported by initialize, with no extra process |
| stderr | redirected only when a callback is set | always redirected and drained, last 400 lines added to exceptions |
| Shutdown | closes stdin, waits 5 s for the CLI to save the session, then kills it | closes stdin, waits 2 s (as in Python), then kills it |
| Types | hand-written records; `UnknownMessage`, `UnknownContentBlock` | generated records, extensible string enums, unions with `Unknown…` variants |

## Public API

| Purpose | Claude .NET | Codex .NET |
|---|---|---|
| Client | `ClaudeAgentClient` | `CodexAgentClient` |
| Options | `ClaudeAgentOptions` | `CodexAgentOptions` |
| Connect | `ConnectAsync` (required, once only) | `ConnectAsync` (optional; the first API call connects) |
| Start a conversation | connecting starts a new session | `StartThreadAsync` |
| Send | `SendAsync(string)` | `thread.RunAsync(RunInput)`, `thread.StartTurnAsync(RunInput)` |
| Receive | `ReceiveResponseAsync`, `ReceiveMessagesAsync` | `turn.StreamAsync`, `turn.RunAsync` |
| Interrupt | `InterruptAsync` | `turn.InterruptAsync` |
| Resume | `ResumeSessionId` option | `ResumeThreadAsync(threadId)` |
| Switch model | `SetModelAsync` | `TurnOptions.Model` per turn |
| Effort | `Effort` option, `ApplyFlagSettingsAsync` | `TurnOptions.Effort` |
| Permissions | `CanUseTool` → `PermissionResultAllow` / `PermissionResultDeny` | `ServerRequestHandler` → protocol response types |
| Server info | `ServerInfo` (`JsonElement?`) | `ServerInfo` (`CodexServerInfo`), `Metadata` |
| Connection state | `IsConnected` | `IsConnected` |
| Raw JSON | `Raw` | `Raw` |
| Base exception | `ClaudeAgentException` | `CodexAgentException` |
| CLI not found | `CliNotFoundException` | `CliNotFoundException` |
| Connection lost | `CliConnectionException` | `TransportClosedException` (as in Python) |

## Shared feature matrix

Legend: ✅ Supported, ⭐ Extra, 🟡 Partial, ❌ No, — N/A (⭐ marks what goes beyond the official SDKs)

### Conversations

| Feature | Claude .NET (PamisWang.ClaudeAgentSdk) | Codex .NET (PamisWang.CodexAgentSdk) |
|---|---|---|
| **Multi-turn conversations** | ✅ keep calling `SendAsync` on the same client | ✅ keep calling `RunAsync` on the same thread |
| **Several conversations at once** | ❌ needs several clients (several CLI processes) | ✅ many threads on one client |
| **Resume a conversation** | ✅ `ResumeSessionId` | ✅ `ResumeThreadAsync` |
| **Fork / list / archive** | ❌ | ✅ |
| **Text streaming** | ✅ `StreamEvent.TextDelta` (with `IncludePartialMessages`) | ✅ `AgentMessageDeltaNotification` |
| **Interrupt** | ✅ | ✅ |
| **Steer a running turn** | ❌ | ✅ `SteerAsync` |
| **Structured output** | 🟡 the result is readable only | ✅ `OutputSchema` |
| **Image input** | ❌ `SendAsync` takes only a string | ✅ `ImageInput`, `LocalImageInput` |

### Models and settings

| Feature | Claude .NET (PamisWang.ClaudeAgentSdk) | Codex .NET (PamisWang.CodexAgentSdk) |
|---|---|---|
| **Choose a model** | ✅ `Model` | ✅ `Model` |
| **Switch model mid-conversation** | ✅ `SetModelAsync` | ✅ per turn |
| **Effort** | ✅ `Effort`, `ApplyFlagSettingsAsync` | ✅ `Effort` |
| **System prompt** | ✅ `SystemPrompt` (default, appended, custom, file) | ✅ `BaseInstructions`, `DeveloperInstructions` |
| **Sandbox** | ❌ | ✅ `Sandbox` |
| **Setting overrides** | 🟡 `ApplyFlagSettingsAsync` | ✅ `ConfigOverrides`, the thread's `Config` |
| **List available models** | 🟡 raw JSON in `ServerInfo` | ✅ `ListModelsAsync` |

### Permissions and account

| Feature | Claude .NET (PamisWang.ClaudeAgentSdk) | Codex .NET (PamisWang.CodexAgentSdk) |
|---|---|---|
| **Permission / approval callback** | ✅ `CanUseTool` | ✅ `ServerRequestHandler` |
| **Allow for this session** | ✅ return `Suggestions` | ✅ `AcceptForSession` |
| **Callback cancelled when withdrawn** | ✅ token cancelled on interrupt | ✅ token cancelled on `serverRequest/resolved` |
| **Sign-in / account** | ❌ reuses the CLI sign-in | ✅ reuses the CLI sign-in; can also sign in and read the account through the API |
| **Rate limit info** | ✅ `RateLimitEvent` | 🟡 the notification is typed but has no public reader |

### Reliability

| Feature | Claude .NET (PamisWang.ClaudeAgentSdk) | Codex .NET (PamisWang.CodexAgentSdk) |
|---|---|---|
| **Version warning** | ✅ `WarningCallback` | ✅ `WarningCallback` |
| **Unrecognized messages / notifications** | ✅ `UnknownMessage` | ✅ `UnknownNotification` |
| **New protocol values** | 🟡 string fields take anything; new content blocks become `UnknownContentBlock` | ✅ extensible enums and `Unknown…` variants |
| **Overload retries** | ❌ | ✅ `CodexRetry` |
| **Refuses Windows batch files** | ✅ | ✅ |

## Naming consistency

| Status | Names |
|---|---|
| Same on both | `…AgentClient`, `…AgentOptions`, `…AgentException`, `ConnectAsync`, `DisposeAsync`, `InterruptAsync`, `IsConnected`, `ServerInfo`, `CliPath`, `WorkingDirectory`, `EnvironmentVariables`, `StandardErrorCallback`, `WarningCallback`, `MaxBufferSize`, `Model`, `Effort`, `Raw`, `CliNotFoundException`, `Unknown…`; internally `ITransport`, `SubprocessTransport`, `NdjsonReader`, `CliLocator`, `SdkInfo` |
| Different on purpose | `CanUseTool` vs `ServerRequestHandler` (Codex server requests are more than tool approvals), `SendAsync` / `ReceiveResponseAsync` vs the thread and turn APIs (different conversation models), `CliConnectionException` vs `TransportClosedException` (each follows its Python SDK) |

## What they could share or borrow

- **Extract a shared base**: `SubprocessTransport` startup and shutdown, `NdjsonReader`, and `CliLocator`'s batch-file check are nearly identical; they could move into a shared internal package (such as `PamisWang.AgentSdk.Core`), or at least be changed together.
- **Recorded fixtures for Codex**: following Claude's `tools/record_fixtures.py`, record initialize, turn, and approval messages from a real app-server so unit tests cover the real format.
- **Claude borrowing Codex's tolerance**: turn known string fields (such as `subtype` and permission modes) into extensible value types so new values don't affect comparisons, and run the permission callback in the background so a slow callback doesn't hold up messages.
- **Consistent stderr handling**: Claude redirects stderr only when a callback is set, while Codex always drains it and keeps the tail; Claude could also keep the tail in exception messages to ease debugging.

