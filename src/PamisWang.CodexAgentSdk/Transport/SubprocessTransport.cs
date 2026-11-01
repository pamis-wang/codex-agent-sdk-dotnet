using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipelines;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;

namespace PamisWang.CodexAgentSdk.Transport;

/// <summary>
/// 以子程序啟動 <c>codex app-server --listen stdio://</c>，透過 stdin、stdout 收發 JSON-RPC。
/// 對應 Python SDK <c>CodexClient</c> 的 <c>start</c>、<c>close</c> 與讀寫。
/// </summary>
internal sealed class SubprocessTransport : ITransport
{
    // 不指定的話 Windows 會用主控台的字碼頁，中文會變亂碼；BOM 會讓 app-server 讀到的第一行解析失敗
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    // 跟 Python SDK 一樣保留 stderr 最後 400 行
    private const int StandardErrorTailLines = 400;

    private readonly CodexAgentOptions _options;
    private readonly CliLocator _locator;
    // stdin 同一時間只能有一個寫入者，不然兩行 JSON 會混在一起
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly Queue<string> _stderrTail = new();
    private readonly Lock _stderrGate = new();

    private Process? _process;
    private PipeReader? _stdout;
    private Stream? _stdin;
    private Task? _stderrPump;
    private volatile bool _closing;

    public SubprocessTransport(CodexAgentOptions options)
        : this(options, CliLocator.ForCurrentProcess())
    {
    }

    internal SubprocessTransport(CodexAgentOptions options, CliLocator locator)
    {
        _options = options;
        _locator = locator;
    }

    /// <summary>關 stdin 後等 app-server 自己結束的時間，跟 Python SDK 一樣 2 秒。測試會調短。</summary>
    internal TimeSpan ShutdownGracePeriod { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>實際啟動的執行檔；檢查沒有版本號的開發版 CLI 支援哪些欄位時，用同一個執行檔產生 schema。</summary>
    internal string? ExecutablePath { get; private set; }

    public string StandardErrorTail
    {
        get
        {
            lock (_stderrGate)
            {
                return string.Join('\n', _stderrTail.TakeLast(40));
            }
        }
    }

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        if (_process is not null)
        {
            return Task.CompletedTask;
        }

        var cliPath = _locator.Find(_options.CliPath);
        _locator.EnsureRunnable(cliPath);

        if (_options.WorkingDirectory is not null && !Directory.Exists(_options.WorkingDirectory))
        {
            throw new CodexAgentException($"工作目錄不存在：{_options.WorkingDirectory}");
        }

        var startInfo = CreateStartInfo(cliPath, _options);
        foreach (var argument in (ReadOnlySpan<string>)["app-server", "--listen", "stdio://"])
        {
            startInfo.ArgumentList.Add(argument);
        }

        Process process;
        try
        {
            process = Process.Start(startInfo)
                ?? throw new CodexAgentException($"無法啟動 Codex：{cliPath}");
        }
        catch (Win32Exception e) when (e.NativeErrorCode == 2)
        {
            // ERROR_FILE_NOT_FOUND／ENOENT
            throw new CliNotFoundException($"找不到 Codex：{cliPath}", e);
        }
        catch (Win32Exception e)
        {
            throw new CodexAgentException($"無法啟動 Codex：{cliPath}（{e.Message}）", e);
        }

        _process = process;
        ExecutablePath = cliPath;
        // stdout 直接讀位元組，由 NdjsonReader 切行後再解碼；stdin 直接寫 UTF-8 位元組
        _stdout = PipeReader.Create(process.StandardOutput.BaseStream);
        _stdin = process.StandardInput.BaseStream;
        // stderr 一定要讀掉：沒人讀的話管線塞滿，app-server 會卡住
        _stderrPump = PumpStandardErrorAsync(process.StandardError);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 建立啟動 <c>codex</c> 的設定，含 <c>--config</c> 覆寫、環境變數與工作目錄；呼叫端再補上子指令。
    /// app-server 與用來產生 schema 的子程序共用，兩者看到的設定才會一致。
    /// </summary>
    internal static ProcessStartInfo CreateStartInfo(string cliPath, CodexAgentOptions options)
    {
        var startInfo = new ProcessStartInfo(cliPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Utf8NoBom,
            StandardErrorEncoding = Utf8NoBom,
            WorkingDirectory = options.WorkingDirectory ?? "",
        };

        // ArgumentList 逐一加上參數，不經過 shell 也不用自己處理引號
        foreach (var setting in options.ConfigOverrides)
        {
            startInfo.ArgumentList.Add("--config");
            startInfo.ArgumentList.Add(setting);
        }

        foreach (var (name, value) in options.EnvironmentVariables)
        {
            startInfo.Environment[name] = value;
        }

        return startInfo;
    }

    public async Task WriteMessageAsync(ReadOnlyMemory<byte> json, CancellationToken cancellationToken)
    {
        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // 檢查都放在鎖裡面，避免檢查完、寫入前被 DisposeAsync 關掉
            if (_stdin is null || _process is null || _closing)
            {
                throw new TransportClosedException("Codex 程序沒有在執行。");
            }

            if (_process.HasExited)
            {
                throw new TransportClosedException($"Codex 程序已經結束（exit code {_process.ExitCode}）。stderr：{StandardErrorTail}");
            }

            try
            {
                await _stdin.WriteAsync(json, cancellationToken).ConfigureAwait(false);
                await _stdin.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
                await _stdin.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (IOException e)
            {
                throw new TransportClosedException("寫入 Codex 的 stdin 失敗。", e);
            }
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async IAsyncEnumerable<JsonElement> ReadMessagesAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (_stdout is not { } stdout)
        {
            throw new TransportClosedException("Codex 程序沒有在執行。");
        }

        await foreach (var message in NdjsonReader.ReadAsync(stdout, _options.MaxBufferSize, cancellationToken)
            .ConfigureAwait(false))
        {
            yield return message;
        }

        // stdout 關了：主動關閉時是預期中的，否則是 app-server 意外結束
        if (!_closing)
        {
            throw new TransportClosedException($"Codex 程序關閉了 stdout。stderr：{StandardErrorTail}");
        }
    }

    /// <summary>關閉 stdin，等 app-server 自己結束；等不到才強制結束整個程序樹。每一步都有時間上限。</summary>
    public async ValueTask DisposeAsync()
    {
        if (_process is not { } process)
        {
            return;
        }

        _closing = true;

        // 拿著寫入鎖關 stdin，避免跟寫入撞在一起；寫入卡在塞滿的管線時也不能讓收尾跟著卡住
        var locked = await _writeLock.WaitAsync(ShutdownGracePeriod).ConfigureAwait(false);
        try
        {
            if (_stdin is not null)
            {
                try
                {
                    await _stdin.DisposeAsync().ConfigureAwait(false);
                }
                catch (IOException)
                {
                    // 程序已經結束，管線斷了；本來就是要關掉它
                }

                _stdin = null;
            }
        }
        finally
        {
            if (locked)
            {
                _writeLock.Release();
            }
        }

        if (!await WaitForExitAsync(process, ShutdownGracePeriod).ConfigureAwait(false))
        {
            try
            {
                // app-server 會開子程序（指令、MCP server），強制結束時一起收掉
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // 剛好在這之間自己結束了
            }

            await WaitForExitAsync(process, ShutdownGracePeriod).ConfigureAwait(false);
        }

        if (_stderrPump is not null)
        {
            await _stderrPump.WaitAsync(ShutdownGracePeriod)
                .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }

        if (_stdout is not null)
        {
            await _stdout.CompleteAsync().ConfigureAwait(false);
        }

        process.Dispose();
        _process = null;
        _stdout = null;
        _stderrPump = null;
    }

    private static async Task<bool> WaitForExitAsync(Process process, TimeSpan timeout)
    {
        using var timeoutSource = new CancellationTokenSource(timeout);
        try
        {
            await process.WaitForExitAsync(timeoutSource.Token).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }

    private async Task PumpStandardErrorAsync(StreamReader stderr)
    {
        try
        {
            while (await stderr.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.TrimEnd();
                if (line.Length == 0)
                {
                    continue;
                }

                lock (_stderrGate)
                {
                    _stderrTail.Enqueue(line);
                    if (_stderrTail.Count > StandardErrorTailLines)
                    {
                        _stderrTail.Dequeue();
                    }
                }

                if (_options.StandardErrorCallback is not { } callback)
                {
                    continue;
                }

                try
                {
                    callback(line);
                }
#pragma warning disable CA1031 // 呼叫端的 callback 出錯不能中斷讀取，不然之後的 stderr 都會不見
                catch (Exception)
#pragma warning restore CA1031
                {
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
            // 程序結束、管線關閉
        }
    }
}
