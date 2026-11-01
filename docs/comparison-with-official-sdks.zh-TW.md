# codex-agent-sdk-dotnet 與官方 Python / TypeScript SDK 對照

[English](comparison-with-official-sdks.md) | 繁體中文

對照 `PamisWang.CodexAgentSdk` 與 OpenAI 官方的 Codex SDK（Python、TypeScript）。.NET 版移植自 Python SDK，兩者都走 `codex app-server`；TypeScript SDK 走的是 `codex exec`。

- 調查日期：2026-11-01
- 官方 Python：openai-codex（openai/codex main 67f0994）
- 官方 TypeScript：@openai/codex-sdk 0.166.0
- .NET：PamisWang.CodexAgentSdk 0.1.0-preview.1
- 協定 schema：Codex CLI 0.166.0
- PyPI 上的 openai-codex 是 0.163.0；.NET 版移植自 repo main 的 Python 原始碼。實機驗證用 Codex CLI 0.166.0（Windows，ChatGPT 登入）。

相關文件：[Claude .NET vs Codex .NET](claude-vs-codex-dotnet.zh-TW.md)。[Claude .NET 與官方 SDK 的對照](https://github.com/pamis-wang/claude-agent-sdk-dotnet/blob/main/docs/comparison-with-official-sdks.zh-TW.md)在 claude-agent-sdk-dotnet repo。

## 重點

1. **兩個官方 SDK 底層不同**：TypeScript 包的是 `codex exec --experimental-json`（每輪一個程序、單向事件）；Python 與 .NET 包的是 `codex app-server`（長駐、JSON-RPC），所以 .NET 的功能面向 Python 看齊。
2. **Python 的公開 API 全部移植**：thread（開新、接續、分岔、列出、封存、讀取、命名、壓縮）、turn（執行、串流、追加指示、中斷）、登入（瀏覽器、device code、API Key）、模型列表、重試。沒移植的是同步 API、只給內部用的 goal 操作、`launch_args_override` 與附帶的 runtime。
3. **核准交給呼叫端**：Python 的核准 handler 只在內部、預設一律 accept；.NET 公開 `ServerRequestHandler`，並多了 `ApprovalMode.User` 把核准請求交給它。handler 在背景執行，不會擋住事件串流，請求被撤回時會取消它的 token。
4. **協定改版時比較耐用**：Python 的 enum 遇到新值、聯集遇到新變體會驗證失敗，整則通知變成 `UnknownNotification`；.NET 的字串列舉保留新值，聯集退回 `Unknown…` 變體，其餘欄位照常解析。
5. **協定型別自動產生**：Python 用 datamodel-code-generator 產生全部 v2 型別（約 14,000 行）；.NET 自寫產生器，只產生 SDK 用得到的 461 個型別（約 10,800 行），並附 source-gen context，維持 AOT 相容。
6. **命名向 Claude .NET 看齊**：`CodexAgentClient`、`CodexAgentOptions`、`ConnectAsync`、`CliPath`、`WarningCallback`、原始 JSON 一律叫 `Raw`；Python 的名稱寫在註解與 README 的對照表。

## 功能矩陣

圖例：✅ 支援、⭐ 加強、🟡 部分、❌ 無、— 不適用（⭐ 表示比官方多做的部分）

### 連線與程序

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **底層通訊** | ✅ `codex app-server --listen stdio://` | 🟡 `codex exec --experimental-json`，單向 JSONL 事件 | ✅ `codex app-server --listen stdio://` |
| **程序生命週期** | ✅ `Codex()` / `AsyncCodex()` 長駐 | 🟡 每次 `run()` 開一個程序 | ✅ 長駐；第一次呼叫或 `ConnectAsync` 時啟動，`DisposeAsync` 關閉 |
| **連線狀態** | 🟡 `metadata` | — | ✅ `IsConnected`、`Metadata`、`ServerInfo` |
| **執行檔** | ✅ `codex_bin`；預設用附帶的 `openai-codex-cli-bin` | ✅ `codexPathOverride`；預設用 `@openai/codex` | 🟡 `CliPath`、PATH、官方安裝位置；不附帶 runtime，Windows 拒絕 `.cmd` |
| **自訂啟動指令** | ✅ `launch_args_override` | ❌ | ❌ |
| **`--config` 覆寫** | ✅ `config_overrides` | ✅ `config`、`configOverrides` | ✅ `ConfigOverrides` |
| **環境變數** | ✅ `env` | ✅ `env` | ✅ `EnvironmentVariables` |
| **stderr** | 🟡 背景讀掉，保留最後 400 行 | 🟡 失敗時附在錯誤訊息 | ⭐ `StandardErrorCallback`，並保留最後 400 行放進例外 |
| **同步 API** | ✅ `Codex` | ❌ | ❌ 只有非同步 |
| **多個 turn 同時串流** | ✅ 依 turn ID 分派 | 🟡 各自一個程序 | ✅ 依 turn ID 分派 |

### 認證與帳號

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **沿用 `codex login`** | ✅ | ✅ | ✅ |
| **ChatGPT 瀏覽器登入** | ✅ `login_chatgpt()` | ❌ | ✅ `LoginChatgptAsync` |
| **Device code 登入** | ✅ `login_chatgpt_device_code()` | ❌ | ✅ `LoginChatgptDeviceCodeAsync` |
| **API Key** | ✅ `login_api_key()` | ✅ `apiKey` 選項 | ✅ `LoginApiKeyAsync` |
| **帳號 / 登出** | ✅ `account()`、`logout()` | ❌ | ✅ `GetAccountAsync`、`LogoutAsync` |

### Thread

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **開新 / 接續** | ✅ `thread_start()`、`thread_resume()` | ✅ `startThread()`、`resumeThread()` | ✅ `StartThreadAsync`、`ResumeThreadAsync` |
| **分岔 / 列出 / 封存 / 還原** | ✅ `thread_fork`、`thread_list`、`thread_archive`、`thread_unarchive` | ❌ | ✅ `ForkThreadAsync`、`ListThreadsAsync`、`ArchiveThreadAsync`、`UnarchiveThreadAsync` |
| **讀取 / 命名 / 壓縮** | ✅ `read()`、`set_name()`、`compact()` | ❌ | ✅ `ReadAsync`、`SetNameAsync`、`CompactAsync` |
| **系統指示** | ✅ `base_instructions`、`developer_instructions` | ❌ | ✅ `BaseInstructions`、`DeveloperInstructions` |
| **臨時 thread** | ✅ `ephemeral` | ❌ | ✅ `Ephemeral` |

### Turn

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **執行並取得結果** | ✅ `run()` → `TurnResult` | ✅ `run()` → `finalResponse`、`items`、`usage` | ✅ `RunAsync` → `TurnResult` |
| **Turn handle** | ✅ `turn()` → `TurnHandle` | 🟡 `runStreamed()` | ✅ `StartTurnAsync` → `TurnHandle` |
| **中斷** | ✅ `interrupt()` | 🟡 `AbortSignal`，直接結束程序 | ⭐ `InterruptAsync`；取消 `RunAsync` 時也會先中斷這一輪 |
| **追加指示** | ✅ `steer()` | ❌ | ✅ `SteerAsync` |
| **輸入種類** | ✅ 文字、圖片、本機圖片、skill、mention、`ExternalMessage` | 🟡 文字、本機圖片 | ✅ 同 Python |
| **結構化輸出** | ✅ `output_schema` | ✅ `outputSchema` | ✅ `OutputSchema` |
| **模型 / 思考深度 / 摘要** | ✅ 每輪可設 | 🟡 只能在 thread 設 | ✅ `TurnOptions` |
| **沙箱** | ✅ `Sandbox` | ✅ `sandboxMode` | ✅ `Sandbox` |
| **服務等級 / 來源標記** | ✅ `service_tier`、`turn_service_tier`、`source` | ❌ | ✅ `ServiceTier`、`TurnServiceTier`、`Source` |
| **最終回覆** | ✅ 取 `final_answer`，否則取沒標階段的最後一則 | ✅ `finalResponse` | ✅ 同 Python |

### 事件串流

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **串流 API** | ✅ `stream()` | ✅ async generator | ✅ `IAsyncEnumerable<Notification>` |
| **逐字增量** | ✅ `item/agentMessage/delta` | ❌ 只有 item 開始、更新、完成 | ✅ `AgentMessageDeltaNotification` |
| **型別化事件** | ✅ 所有 server 通知都有型別 | 🟡 exec 事件 8 種 | ✅ 85 種通知（CLI 0.166.0）都有型別 |
| **不認得的通知** | ✅ `UnknownNotification`（`.params`） | ❌ | ✅ `UnknownNotification`（`.Raw`） |
| **保留原始 JSON** | 🟡 只有不認得的通知 | ❌ | ⭐ 每則通知都有 `Raw` |
| **協定新增的 enum 值** | ❌ 驗證失敗，整則變成未知通知 | — | ⭐ 保留新值，照常比較 |
| **協定新增的聯集變體** | ❌ 驗證失敗 | — | ⭐ 退回 `Unknown…` 變體，保留原始 JSON |
| **turn/start 回應前的事件** | ✅ 先暫存（`pending_turn`） | — | ✅ 先暫存 |

### 核准與互動

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **核准模式** | 🟡 `auto_review`、`deny_all` | 🟡 `approvalPolicy` | ⭐ `AutoReview`、`DenyAll`、`User` |
| **自訂核准 handler** | ❌ 只在內部，預設一律 accept | ❌ | ⭐ 公開的 `ServerRequestHandler` |
| **沒給 handler 時** | ✅ 指令與檔案變更一律 accept | — | ✅ 同 Python |
| **handler 執行中的事件** | ❌ 讀取迴圈停住 | — | ⭐ 背景執行，事件照常送達 |
| **handler 丟例外** | ❌ 讀取迴圈結束，連線失效 | — | ⭐ 以 JSON-RPC 錯誤回覆，連線不受影響 |
| **請求被撤回** | ❌ | — | ⭐ 收到 `serverRequest/resolved` 就取消 handler 的 token |

### 版本、錯誤與重試

| 功能 | 官方 Python（openai-codex） | 官方 TypeScript（@openai/codex-sdk） | .NET（PamisWang.CodexAgentSdk） |
|---|---|---|---|
| **最低版本** | ✅ 0.151.0，送出較新的欄位前檢查 | ❌ | ⭐ 同 Python，另有連線時的 `WarningCallback` |
| **沒有版本號的開發版** | ✅ 產生 schema 檢查欄位 | — | ✅ 同 Python |
| **錯誤型別** | ✅ `JsonRpcError` 及其子類別 | 🟡 一般的 `Error` | ✅ `JsonRpcException` 及其子類別 |
| **過載重試** | ✅ `retry_on_overload` | ❌ | ⭐ `CodexRetry`，也認得 v2 的 `serverOverloaded` |
| **turn 失敗** | 🟡 `RuntimeError` | ✅ `turn.failed` 事件 | ⭐ `TurnFailedException`，附上整個 `Turn` |

## 規模與品質

| 項目 | 官方 Python | 官方 TypeScript | .NET |
|---|---|---|---|
| 手寫程式碼 | 約 4,200 行 | 約 1,070 行 | 約 3,870 行 C#（36 個檔案） |
| 產生的協定型別 | 約 14,200 行（全部 v2 型別） | 手寫 `events.ts`、`items.ts` | 約 10,800 行（461 個型別） |
| 測試 | app-server harness 與真實整合測試 | 假的 Responses API proxy | 77 個單元測試（假 app-server）＋ 3 個真實整合測試 |
| 實機驗證 | — | — | Codex CLI 0.166.0：串流、`RunAsync`、核准流程（拒絕寫檔）都通過 |
| 授權 | Apache-2.0 | Apache-2.0 | Apache-2.0，NOTICE 註明移植來源 |
| AOT | — | — | `IsAotCompatible`，編譯 0 警告 |

## 目前限制

- 不附帶 Codex runtime，要自己安裝 Codex CLI（0.151.0 以上）；Windows 要用原生的 `codex.exe`。
- 只有非同步 API；沒有 Python 的同步版 `Codex`。
- 沒有公開的通用通知讀取 API：不屬於任何 turn 的通知（例如 `account/updated`）目前只保留最近 1,000 則，沒有讀取方法。
- 沒有公開的低階請求 API，呼叫還沒包裝的 app-server 方法（約 180 個中的大多數）要等之後補。
- `thread/start.dynamicTools`（SDK 內自訂工具）是 experimental，等協定穩定再評估。

