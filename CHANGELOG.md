# Changelog

## 0.1.0-preview.1（2026-11-01）

第一個預覽版。移植自 openai/codex 的 Python SDK（`sdk/python` @ 67f0994），協定型別產生自 Codex CLI 0.166.0 的 schema。

- `CodexAgentClient`：啟動 `codex app-server --listen stdio://`，第一次呼叫 API 時自動連線，也可以先呼叫 `ConnectAsync`。
- Thread：`StartThreadAsync`、`ResumeThreadAsync`、`ForkThreadAsync`、`ListThreadsAsync`、`ArchiveThreadAsync`、
  `UnarchiveThreadAsync`；`CodexThread` 的 `ReadAsync`、`SetNameAsync`、`CompactAsync`。
- Turn：`CodexThread.RunAsync` 執行並收集 `TurnResult`（取消時會中斷這一輪）；`StartTurnAsync` 回傳 `TurnHandle`，
  可以 `StreamAsync`、`SteerAsync`、`InterruptAsync`。輸入支援文字、圖片、本機圖片、skill、mention 與 `ExternalMessage`。
- 登入與帳號：沿用 `codex login`；`LoginChatgptAsync`、`LoginChatgptDeviceCodeAsync`、`LoginApiKeyAsync`、`GetAccountAsync`、`LogoutAsync`。
- 核准：`ServerRequestHandler` 處理 app-server 送來的請求，在背景執行，請求被撤回時取消；`ApprovalMode.User` 把核准交給它。
- 版本：`WarningCallback` 在連線時提醒 CLI 版本低於 0.151.0；較新的選項送出前也會檢查版本。
- 協定型別：`PamisWang.CodexAgentSdk.Protocol`，由 `tools/generate_protocol.py` 產生；字串列舉可擴充，
  聯集遇到不認得的變體退回 `Unknown…`。
- 錯誤：`CodexAgentException`、`JsonRpcException` 及其子類別、`TransportClosedException`、`TurnFailedException`；
  `CodexRetry` 處理 server 過載。
- 對照 Codex CLI 0.166.0、Python SDK `67f0994`。
