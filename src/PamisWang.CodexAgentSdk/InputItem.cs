namespace PamisWang.CodexAgentSdk;

/// <summary>turn 的一項輸入。對應 Python SDK 的 <c>InputItem</c>。</summary>
public abstract record InputItem;

/// <summary>文字輸入。對應 Python SDK 的 <c>TextInput</c>。</summary>
/// <param name="Text">文字。</param>
public sealed record TextInput(string Text) : InputItem;

/// <summary>
/// 圖片輸入，以 base64 編碼的 <c>data:image/...</c> URL 給。對應 Python SDK 的 <c>ImageInput</c>。
/// 不建議用 HTTP 網址；請下載後改用 <see cref="LocalImageInput"/>。
/// </summary>
/// <param name="Url">圖片的 data URL。</param>
public sealed record ImageInput(string Url) : InputItem;

/// <summary>本機圖片檔。對應 Python SDK 的 <c>LocalImageInput</c>。</summary>
/// <param name="Path">圖片檔的路徑。</param>
public sealed record LocalImageInput(string Path) : InputItem;

/// <summary>指定使用某個 skill。對應 Python SDK 的 <c>SkillInput</c>。</summary>
/// <param name="Name">skill 名稱。</param>
/// <param name="Path">skill 的路徑。</param>
public sealed record SkillInput(string Name, string Path) : InputItem;

/// <summary>提及某個資源。對應 Python SDK 的 <c>MentionInput</c>。</summary>
/// <param name="Name">資源名稱。</param>
/// <param name="Path">資源的路徑。</param>
public sealed record MentionInput(string Name, string Path) : InputItem;
