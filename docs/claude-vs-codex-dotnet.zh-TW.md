# claude-agent-sdk-dotnet 與 codex-agent-sdk-dotnet 對照

[English](claude-vs-codex-dotnet.md) | 繁體中文

兩個 SDK 都是把官方 Python SDK 移植成 .NET，啟動本機 CLI 再以子程序溝通；但底層協定、對話模型與型別來源不同。這份報告比較兩者的架構、API 與命名，並整理可以共用或互相借鏡的地方。

- 調查日期：2026-11-01
- Claude：PamisWang.ClaudeAgentSdk 0.1.0-preview.5
- Codex：PamisWang.CodexAgentSdk 0.1.0-preview.1

相關文件：[Codex .NET vs 官方](comparison-with-official-sdks.zh-TW.md)。[Claude .NET 與官方 SDK 的對照](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/docs/comparison-with-official-sdks.zh-TW.md)在 claude-agent-sdk-dotnet repo。

## 重點

1. **同一套骨架**：兩者都是 .NET 10、零相依、AOT 相容、`TreatWarningsAsErrors`，共用同一份 `.editorconfig` 與建置設定；`SubprocessTransport`、`NdjsonReader`、`CliLocator`（拒絕 Windows 批次檔）幾乎一樣。
2. **協定差最多**：Claude 是 stream-json 加 control protocol，一個 client 就是一段對話；Codex 是 JSON-RPC（不帶 `jsonrpc` 欄位），一個 client 可以同時開多個 thread、多個 turn。
3. **型別來源不同**：Claude 沒有官方 schema，訊息型別手寫並用真實 CLI 錄下的樣本測試；Codex 有 `generate-json-schema`，461 個協定型別由產生器產生。
4. **命名已經對齊**：Client、Options、例外基底、`ConnectAsync`、`CliPath`、`WarningCallback`、`StandardErrorCallback`、`MaxBufferSize`、原始 JSON 的 `Raw` 兩邊一致；刻意不同的地方都源自協定差異。
5. **測試策略各有長處**：Claude 有 190 個測試與錄製的協定樣本，回歸保護較強；Codex 有假 app-server 與可選的真實整合測試，可以直接驗證真實 CLI。
6. **可以互相借鏡**：Codex 可以補上從真實 CLI 錄下的協定樣本；Claude 可以參考 Codex 的「未知值不讓解析失敗」與背景執行的權限 handler。

## 基本資料

| 項目 | Claude .NET | Codex .NET |
|---|---|---|
| 套件 | `PamisWang.ClaudeAgentSdk` 0.1.0-preview.5 | `PamisWang.CodexAgentSdk` 0.1.0-preview.1 |
| 移植來源 | claude-agent-sdk-python v0.2.174（MIT） | openai/codex `sdk/python` @ 67f0994（Apache-2.0） |
| 本身的授權 | MIT | Apache-2.0 |
| 底層 CLI | Claude Code CLI 2.1.312，最低 2.0.0 | Codex CLI 0.166.0，最低 0.151.0 |
| 目標框架 / 相依 | .NET 10，沒有相依套件 | .NET 10，沒有相依套件 |
| AOT | `IsAotCompatible` | `IsAotCompatible`（協定型別附 source-gen context） |
| 手寫程式碼 | 約 2,560 行（48 個檔案） | 約 3,870 行（36 個檔案） |
| 產生的程式碼 | 無 | 約 10,800 行（`tools/generate_protocol.py`） |
| 測試 | 190 個；錄製樣本 + 假 CLI 子程序 | 77 個單元 + 3 個真實整合（需 `CODEX_INTEGRATION_TESTS=1`） |

## 架構對照

| 面向 | Claude .NET | Codex .NET |
|---|---|---|
| 啟動的指令 | `claude --output-format stream-json --verbose … --input-format stream-json` | `codex app-server --listen stdio://` |
| 訊息格式 | 一行一則 JSON；使用者訊息、助理訊息、control request / response | 一行一則 JSON-RPC 2.0 風格，沒有 `jsonrpc` 欄位 |
| 對話模型 | 一個 client 一個 session，依序收訊息 | 一個 client 多個 thread，每個 thread 多個 turn，可同時進行 |
| 請求配對 | control request 的 `request_id` | JSON-RPC `id`（GUID 字串） |
| 事件分派 | 單一讀取者，依序交給呼叫端 | `MessageRouter` 依 turn ID 分派，每個訂閱者各自有游標 |
| 權限 / 核准 | CLI 送 `can_use_tool` control request，交給 `CanUseTool` | server 送 JSON-RPC request，交給 `ServerRequestHandler`（背景執行） |
| 初始化 | control `initialize`，回傳 `ServerInfo`（原始 JSON） | `initialize` → `initialized`，回傳 `InitializeResponse` 與 `CodexServerInfo` |
| 版本檢查 | 連線前執行 `claude -v` | 讀 initialize 回報的 `userAgent`，不另外啟動程序 |
| stderr | 有 callback 才導向 | 一律導向並讀掉，保留最後 400 行放進例外 |
| 關閉 | 關 stdin，等 5 秒讓 CLI 寫完 session，再強制結束 | 關 stdin，等 2 秒（同 Python），再強制結束 |
| 型別 | 手寫 record；`UnknownMessage`、`UnknownContentBlock` | 產生的 record、可擴充字串列舉、聯集與 `Unknown…` 變體 |

## 公開 API 對照

| 用途 | Claude .NET | Codex .NET |
|---|---|---|
| Client | `ClaudeAgentClient` | `CodexAgentClient` |
| 設定 | `ClaudeAgentOptions` | `CodexAgentOptions` |
| 連線 | `ConnectAsync`（必須先呼叫，只能一次） | `ConnectAsync`（可省略，第一次呼叫 API 時自動連線） |
| 開始對話 | 連線即是新 session | `StartThreadAsync` |
| 送出訊息 | `SendAsync(string)` | `thread.RunAsync(RunInput)`、`thread.StartTurnAsync(RunInput)` |
| 接收 | `ReceiveResponseAsync`、`ReceiveMessagesAsync` | `turn.StreamAsync`、`turn.RunAsync` |
| 中斷 | `InterruptAsync` | `turn.InterruptAsync` |
| 接續 | `ResumeSessionId` 選項 | `ResumeThreadAsync(threadId)` |
| 換模型 | `SetModelAsync` | 每輪的 `TurnOptions.Model` |
| 思考深度 | `Effort` 選項、`ApplyFlagSettingsAsync` | `TurnOptions.Effort` |
| 權限 | `CanUseTool` → `PermissionResultAllow` / `PermissionResultDeny` | `ServerRequestHandler` → 協定的 response 型別 |
| 伺服器資訊 | `ServerInfo`（`JsonElement?`） | `ServerInfo`（`CodexServerInfo`）、`Metadata` |
| 連線狀態 | `IsConnected` | `IsConnected` |
| 原始 JSON | `Raw` | `Raw` |
| 例外基底 | `ClaudeAgentException` | `CodexAgentException` |
| 找不到 CLI | `CliNotFoundException` | `CliNotFoundException` |
| 連線中斷 | `CliConnectionException` | `TransportClosedException`（照 Python） |

## 共通功能矩陣

圖例：✅ 支援、⭐ 加強、🟡 部分、❌ 無、— 不適用（⭐ 表示比官方多做的部分）

### 對話

| 功能 | Claude .NET（PamisWang.ClaudeAgentSdk） | Codex .NET（PamisWang.CodexAgentSdk） |
|---|---|---|
| **多輪對話** | ✅ 同一個 client 接著 `SendAsync` | ✅ 同一個 thread 接著 `RunAsync` |
| **同時多段對話** | ❌ 要開多個 client（多個 CLI 程序） | ✅ 一個 client 多個 thread |
| **接續舊對話** | ✅ `ResumeSessionId` | ✅ `ResumeThreadAsync` |
| **分岔 / 列出 / 封存** | ❌ | ✅ |
| **逐字串流** | ✅ `StreamEvent.TextDelta`（要開 `IncludePartialMessages`） | ✅ `AgentMessageDeltaNotification` |
| **中斷** | ✅ | ✅ |
| **執行中追加指示** | ❌ | ✅ `SteerAsync` |
| **結構化輸出** | 🟡 只能讀結果 | ✅ `OutputSchema` |
| **圖片輸入** | ❌ `SendAsync` 只收字串 | ✅ `ImageInput`、`LocalImageInput` |

### 模型與設定

| 功能 | Claude .NET（PamisWang.ClaudeAgentSdk） | Codex .NET（PamisWang.CodexAgentSdk） |
|---|---|---|
| **指定模型** | ✅ `Model` | ✅ `Model` |
| **對話中換模型** | ✅ `SetModelAsync` | ✅ 每輪指定 |
| **思考深度** | ✅ `Effort`、`ApplyFlagSettingsAsync` | ✅ `Effort` |
| **系統提示詞** | ✅ `SystemPrompt`（預設、附加、自訂、檔案） | ✅ `BaseInstructions`、`DeveloperInstructions` |
| **沙箱** | ❌ | ✅ `Sandbox` |
| **設定覆寫** | 🟡 `ApplyFlagSettingsAsync` | ✅ `ConfigOverrides`、thread 的 `Config` |
| **列出可用模型** | 🟡 `ServerInfo` 原始 JSON | ✅ `ListModelsAsync` |

### 權限與帳號

| 功能 | Claude .NET（PamisWang.ClaudeAgentSdk） | Codex .NET（PamisWang.CodexAgentSdk） |
|---|---|---|
| **權限 / 核准回呼** | ✅ `CanUseTool` | ✅ `ServerRequestHandler` |
| **「這次都允許」** | ✅ 回傳 `Suggestions` | ✅ `AcceptForSession` |
| **回呼被撤回時取消** | ✅ 中斷時取消 token | ✅ `serverRequest/resolved` 時取消 token |
| **登入 / 帳號** | ❌ 沿用 CLI 登入 | ✅ 沿用 CLI 登入，也能用 API 登入、讀帳號 |
| **額度資訊** | ✅ `RateLimitEvent` | 🟡 通知有型別，但沒有公開讀取方法 |

### 可靠性

| 功能 | Claude .NET（PamisWang.ClaudeAgentSdk） | Codex .NET（PamisWang.CodexAgentSdk） |
|---|---|---|
| **版本警告** | ✅ `WarningCallback` | ✅ `WarningCallback` |
| **不認得的訊息 / 通知** | ✅ `UnknownMessage` | ✅ `UnknownNotification` |
| **協定新增的值** | 🟡 字串欄位照收；新的內容區塊變 `UnknownContentBlock` | ✅ 可擴充列舉與 `Unknown…` 變體 |
| **過載重試** | ❌ | ✅ `CodexRetry` |
| **Windows 拒絕批次檔** | ✅ | ✅ |

## 命名一致性

| 狀態 | 名稱 |
|---|---|
| 兩邊一致 | `…AgentClient`、`…AgentOptions`、`…AgentException`、`ConnectAsync`、`DisposeAsync`、`InterruptAsync`、`IsConnected`、`ServerInfo`、`CliPath`、`WorkingDirectory`、`EnvironmentVariables`、`StandardErrorCallback`、`WarningCallback`、`MaxBufferSize`、`Model`、`Effort`、`Raw`、`CliNotFoundException`、`Unknown…`；內部的 `ITransport`、`SubprocessTransport`、`NdjsonReader`、`CliLocator`、`SdkInfo` |
| 刻意不同 | `CanUseTool` 對 `ServerRequestHandler`（Codex 的 server 請求不只工具核准）、`SendAsync` / `ReceiveResponseAsync` 對 thread / turn 的 API（對話模型不同）、`CliConnectionException` 對 `TransportClosedException`（各自照 Python） |

## 可以共用或互相借鏡

- **抽出共用的基礎**：`SubprocessTransport` 的啟動與收尾、`NdjsonReader`、`CliLocator` 的批次檔檢查幾乎一樣，可以抽成共用的內部套件（例如 `PamisWang.AgentSdk.Core`），或至少保持兩邊同步修改。
- **Codex 補錄製樣本**：照 Claude 的 `tools/record_fixtures.py`，從真實 app-server 錄下 initialize、turn、核准的訊息，讓單元測試覆蓋真實格式。
- **Claude 參考 Codex 的耐用性**：把已知的字串欄位（例如 `subtype`、權限模式）改成可擴充的值型別，新值不影響比較；權限回呼改在背景執行，避免慢的回呼擋住訊息。
- **stderr 行為統一**：Claude 只有給 callback 才導向 stderr，Codex 一律讀掉並保留尾段；可以讓 Claude 也保留尾段放進例外訊息，方便除錯。

