using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using System.Windows.Forms;

namespace PCControlAgent;

internal static class Program
{
    [STAThread]
    private static async Task Main(string[] args)
    {
        using var mutex = new Mutex(true, "Global\\190x4_PCControlAgent", out var first);
        if (!first) return;

        var config = AgentConfig.Load();
        var userArg = ReadArg(args, "--user-id");
        if (long.TryParse(userArg, out var userId) && userId > 0)
        {
            config.UserId = userId;
            config.Save();
        }
        if (config.UserId <= 0) return;
        LegacyConfigMigration.TryApply(config);

        Directory.CreateDirectory(AppPaths.Root);
        await using var log = new AgentLog(AppPaths.Log);
        await log.WriteAsync($"Агент запущен, версия {AgentConfig.CurrentVersion}");
        NativeActions.RecoverHiddenTaskManager();

        var agent = new ControlAgent(config, log);
        await agent.RunAsync();
    }

    private static string? ReadArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }
}

internal static class LegacyConfigMigration
{
    public static void TryApply(AgentConfig config)
    {
        if (!string.IsNullOrWhiteSpace(config.ObsPassword)) return;
        const string legacyPath = @"C:\pc-control.ps1";
        try
        {
            if (!File.Exists(legacyPath)) return;
            var source = File.ReadAllText(legacyPath);
            var branchPattern = "(?:if|elseif)\\s*\\(\\$USER_ID\\s*-eq\\s*[\"']"
                                + Regex.Escape(config.UserId.ToString())
                                + "[\"']\\)\\s*\\{(?<body>.*?)\\n\\}";
            var branch = Regex.Match(
                source,
                branchPattern,
                RegexOptions.Singleline | RegexOptions.IgnoreCase);
            if (!branch.Success) return;
            var body = branch.Groups["body"].Value;
            var password = Regex.Match(body, "\\$OBS_WS_PASSWORD\\s*=\\s*[\"'](?<v>.*?)[\"']");
            var path = Regex.Match(body, "\\$OBS_PATH\\s*=\\s*[\"'](?<v>.*?)[\"']");
            var records = Regex.Match(body, "\\$OBS_RECORD_PATH\\s*=\\s*[\"'](?<v>.*?)[\"']");
            if (password.Success) config.ObsPassword = password.Groups["v"].Value;
            if (path.Success) config.ObsPath = path.Groups["v"].Value;
            if (records.Success) config.ObsRecordPath = records.Groups["v"].Value;
            config.Save();
        }
        catch { }
    }
}

internal static class AppPaths
{
    public static readonly string Root = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "190x4", "PCControl");
    public static readonly string Config = Path.Combine(Root, "config.json");
    public static readonly string Log = Path.Combine(Root, "agent.log");
    public static readonly string V2Marker = Path.Combine(Root, "v2-active");
}

internal sealed class AgentConfig
{
    public const string CurrentVersion = "2.0.2";
    public long UserId { get; set; }
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceName { get; set; } = Environment.MachineName;
    public string Server { get; set; } = "https://190x4.pw";
    public string ProtectedToken { get; set; } = "";
    public string PairingSecret { get; set; } = "";
    public string PairingCode { get; set; } = "";
    public string ObsPassword { get; set; } = "";
    public string ObsPath { get; set; } = @"C:\Program Files\obs-studio\bin\64bit\obs64.exe";
    public string ObsRecordPath { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyVideos));

    [JsonIgnore]
    public string AgentToken
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ProtectedToken)) return "";
            try
            {
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(ProtectedToken), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            catch { return ""; }
        }
        set
        {
            var bytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), null, DataProtectionScope.CurrentUser);
            ProtectedToken = Convert.ToBase64String(bytes);
        }
    }

    public static AgentConfig Load()
    {
        try
        {
            if (File.Exists(AppPaths.Config))
                return JsonSerializer.Deserialize<AgentConfig>(File.ReadAllText(AppPaths.Config))
                       ?? new AgentConfig();
        }
        catch { }
        return new AgentConfig();
    }

    public void Save()
    {
        Directory.CreateDirectory(AppPaths.Root);
        var temp = AppPaths.Config + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(this, JsonOptions.Indented));
        File.Move(temp, AppPaths.Config, true);
    }
}

internal static class JsonOptions
{
    public static readonly JsonSerializerOptions Default = new(JsonSerializerDefaults.Web);
    public static readonly JsonSerializerOptions Indented = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };
}

internal sealed class AgentLog : IAsyncDisposable
{
    private readonly string _path;
    private readonly SemaphoreSlim _lock = new(1, 1);
    public AgentLog(string path) => _path = path;

    public async Task WriteAsync(string message)
    {
        await _lock.WaitAsync();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            await File.AppendAllTextAsync(_path, $"{DateTime.Now:dd.MM.yyyy HH:mm:ss} — {message}{Environment.NewLine}");
            var lines = await File.ReadAllLinesAsync(_path);
            if (lines.Length > 500) await File.WriteAllLinesAsync(_path, lines[^500..]);
        }
        catch { }
        finally { _lock.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        _lock.Dispose();
        return ValueTask.CompletedTask;
    }
}

internal sealed class ControlAgent
{
    private readonly AgentConfig _config;
    private readonly AgentLog _log;
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private CancellationTokenSource? _streamCts;
    private ClientWebSocket? _socket;
    private DateTime _nextUpdateCheck = DateTime.MinValue;

    public ControlAgent(AgentConfig config, AgentLog log)
    {
        _config = config;
        _log = log;
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"190x4-PCControl/{AgentConfig.CurrentVersion}");
    }

    public async Task RunAsync()
    {
        while (true)
        {
            try
            {
                if (DateTime.UtcNow >= _nextUpdateCheck)
                {
                    _nextUpdateCheck = DateTime.UtcNow.AddHours(1);
                    await CheckForUpdateAsync();
                }
                if (string.IsNullOrWhiteSpace(_config.AgentToken))
                {
                    await EnsureEnrolledAsync();
                    await Task.Delay(TimeSpan.FromSeconds(5));
                    continue;
                }
                await RunSocketAsync();
            }
            catch (Exception ex)
            {
                await _log.WriteAsync($"Соединение: {ex.Message}");
            }
            await Task.Delay(TimeSpan.FromSeconds(5));
        }
    }

    private async Task CheckForUpdateAsync()
    {
        try
        {
            var version = (await _http.GetStringAsync($"{_config.Server}/pc-control-agent-version.txt")).Trim();
            if (!Version.TryParse(version, out var remote)
                || !Version.TryParse(AgentConfig.CurrentVersion, out var current)
                || remote <= current) return;

            var expected = (await _http.GetStringAsync($"{_config.Server}/pc-control-agent.sha256"))
                .Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].ToLowerInvariant();
            var bytes = await _http.GetByteArrayAsync($"{_config.Server}/PCControlAgent.exe");
            var actual = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(expected) || !CryptographicOperations.FixedTimeEquals(
                    Encoding.ASCII.GetBytes(expected), Encoding.ASCII.GetBytes(actual)))
                throw new InvalidOperationException("SHA256 обновления не совпал");

            var currentPath = Environment.ProcessPath ?? throw new InvalidOperationException("Путь агента не найден");
            var nextPath = Path.Combine(AppPaths.Root, "PCControlAgent.next.exe");
            await File.WriteAllBytesAsync(nextPath, bytes);
            var script = Path.Combine(AppPaths.Root, "apply-update.ps1");
            var ps = $"Start-Sleep -Seconds 2; Move-Item -LiteralPath '{nextPath.Replace("'", "''")}' "
                     + $"-Destination '{currentPath.Replace("'", "''")}' -Force; "
                     + $"Start-Process -FilePath '{currentPath.Replace("'", "''")}' -ArgumentList '--user-id','{_config.UserId}'";
            await File.WriteAllTextAsync(script, ps, Encoding.UTF8);
            Process.Start(new ProcessStartInfo("powershell.exe",
                $"-NoProfile -NonInteractive -WindowStyle Hidden -ExecutionPolicy Bypass -File \"{script}\"")
            {
                UseShellExecute = false,
                CreateNoWindow = true
            });
            await _log.WriteAsync($"Устанавливается обновление {remote}");
            Environment.Exit(0);
        }
        catch (HttpRequestException) { }
        catch (Exception ex) { await _log.WriteAsync($"Обновление: {ex.Message}"); }
    }

    private async Task EnsureEnrolledAsync()
    {
        if (string.IsNullOrWhiteSpace(_config.PairingSecret))
        {
            var payload = JsonSerializer.Serialize(new
            {
                user_id = _config.UserId,
                device_id = _config.DeviceId,
                device_name = _config.DeviceName,
                version = AgentConfig.CurrentVersion
            });
            using var response = await _http.PostAsync(
                $"{_config.Server}/api/pc/v2/enroll/start",
                new StringContent(payload, Encoding.UTF8, "application/json"));
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"Сопряжение: HTTP {(int)response.StatusCode}");
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            _config.PairingSecret = doc.RootElement.GetProperty("pairing_secret").GetString() ?? "";
            _config.PairingCode = doc.RootElement.GetProperty("pairing_code").GetString() ?? "";
            _config.Save();
            await _log.WriteAsync($"Ожидается подтверждение в Telegram, код {_config.PairingCode}");
            return;
        }

        using var request = new HttpRequestMessage(
            HttpMethod.Get,
            $"{_config.Server}/api/pc/v2/enroll/status?device_id={Uri.EscapeDataString(_config.DeviceId)}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.PairingSecret);
        using var responseStatus = await _http.SendAsync(request);
        if ((int)responseStatus.StatusCode == 410)
        {
            _config.PairingSecret = "";
            _config.PairingCode = "";
            _config.Save();
            return;
        }
        if (!responseStatus.IsSuccessStatusCode) return;
        using var statusDoc = JsonDocument.Parse(await responseStatus.Content.ReadAsStringAsync());
        if (!statusDoc.RootElement.TryGetProperty("agent_token", out var tokenNode)) return;
        var token = tokenNode.GetString();
        if (string.IsNullOrWhiteSpace(token)) return;
        _config.AgentToken = token;
        _config.PairingSecret = "";
        _config.PairingCode = "";
        _config.Save();
        File.WriteAllText(AppPaths.V2Marker, DateTime.UtcNow.ToString("O"));
        await _log.WriteAsync("Сопряжение подтверждено, защищённый канал активирован");
    }

    private async Task RunSocketAsync()
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", $"Bearer {_config.AgentToken}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        var uri = new Uri(_config.Server.Replace("https://", "wss://") + "/api/pc/v2/agent/ws");
        await socket.ConnectAsync(uri, CancellationToken.None);
        _socket = socket;
        await _log.WriteAsync("Защищённый канал подключён");

        var buffer = new byte[256 * 1024];
        while (socket.State == WebSocketState.Open)
        {
            using var memory = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close) return;
                memory.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);
            if (result.MessageType != WebSocketMessageType.Text) continue;
            var json = Encoding.UTF8.GetString(memory.ToArray());
            await HandleMessageAsync(json);
        }
    }

    private async Task HandleMessageAsync(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.GetProperty("type").GetString() != "command") return;
        var id = root.GetProperty("id").GetString() ?? "";
        var action = root.GetProperty("action").GetString() ?? "";
        var expires = root.GetProperty("expires_at").GetInt64();
        if (DateTimeOffset.UtcNow.ToUnixTimeSeconds() > expires)
        {
            await SendAckAsync(id, false, "Команда просрочена");
            return;
        }
        try
        {
            var payload = root.TryGetProperty("payload", out var node) ? node : default;
            await ExecuteAsync(action, payload);
            await SendAckAsync(id, true, null);
        }
        catch (Exception ex)
        {
            await _log.WriteAsync($"Команда {action}: {ex.Message}");
            await SendAckAsync(id, false, ex.Message);
        }
    }

    private async Task ExecuteAsync(string action, JsonElement payload)
    {
        switch (action)
        {
            case "shutdown": NativeActions.Start("shutdown.exe", "/s /t 60", hidden: true); break;
            case "reboot": NativeActions.Start("shutdown.exe", "/r /t 60", hidden: true); break;
            case "hibernate": Application.SetSuspendState(PowerState.Hibernate, true, false); break;
            case "sleep": Application.SetSuspendState(PowerState.Suspend, true, false); break;
            case "lock": NativeActions.LockWorkStation(); break;
            case "monitor_off": NativeActions.MonitorOff(); break;
            case "mute": AudioController.SetMute(true); break;
            case "unmute": AudioController.SetMute(false); break;
            case "vol_down": AudioController.ChangeVolume(-0.10f); break;
            case "vol_up": AudioController.ChangeVolume(0.10f); break;
            case "media_play": NativeActions.Key(0xB3); break;
            case "media_next": NativeActions.Key(0xB0); break;
            case "media_prev": NativeActions.Key(0xB1); break;
            case "fullscreen": NativeActions.Key(0x7A); break;
            case "win_d": NativeActions.Combo(0x5B, 0x44); break;
            case "alt_tab": NativeActions.Combo(0x12, 0x09); break;
            case "search": NativeActions.Combo(0x5B, 0x53); break;
            case "taskmgr": NativeActions.Start("taskmgr.exe"); break;
            case "open_browser": NativeActions.Start("explorer.exe", "https://www.google.com"); break;
            case "open_explorer": NativeActions.Start("explorer.exe"); break;
            case "open_terminal": NativeActions.Start("cmd.exe"); break;
            case "open_notepad": NativeActions.Start("notepad.exe"); break;
            case "clipboard":
                var text = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("text", out var value)
                    ? value.GetString() ?? "" : "";
                Clipboard.SetText(text);
                break;
            case "screenshot": await UploadScreenshotAsync(); break;
            case "screen_stream_start": StartScreenStream(payload); break;
            case "screen_stream_stop": StopScreenStream(); break;
            case "stream_start": await ObsAsync("StartStream"); break;
            case "stream_stop": await ObsAsync("StopStream"); break;
            case "record_start": await ObsAsync("StartRecord"); break;
            case "record_stop": await ObsAsync("StopRecord"); break;
            case "replay_start": await ObsAsync("StartReplayBuffer"); break;
            case "replay_stop": await ObsAsync("StopReplayBuffer"); break;
            case "record_highlight": await ObsAsync("SaveReplayBuffer"); break;
            default: throw new InvalidOperationException("Неизвестная команда");
        }
    }

    private async Task UploadScreenshotAsync()
    {
        var jpeg = ScreenCapture.Capture(1920, 78);
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.Server}/api/pc/v2/agent/screenshot");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AgentToken);
        request.Content = new ByteArrayContent(jpeg);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
        using var response = await _http.SendAsync(request);
        response.EnsureSuccessStatusCode();
    }

    private void StartScreenStream(JsonElement payload)
    {
        StopScreenStream();
        var fps = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("fps", out var fpsNode)
            ? Math.Clamp(fpsNode.GetInt32(), 1, 5) : 2;
        var quality = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("quality", out var qNode)
            ? Math.Clamp(qNode.GetInt32(), 35, 80) : 55;
        var width = payload.ValueKind == JsonValueKind.Object && payload.TryGetProperty("max_width", out var wNode)
            ? Math.Clamp(wNode.GetInt32(), 640, 1920) : 1280;
        _streamCts = new CancellationTokenSource();
        _ = Task.Run(() => StreamLoopAsync(fps, quality, width, _streamCts.Token));
    }

    private void StopScreenStream()
    {
        _streamCts?.Cancel();
        _streamCts?.Dispose();
        _streamCts = null;
    }

    private async Task StreamLoopAsync(int fps, int quality, int width, CancellationToken token)
    {
        while (!token.IsCancellationRequested && _socket?.State == WebSocketState.Open)
        {
            try
            {
                var frame = ScreenCapture.Capture(width, quality);
                await SendBinaryAsync(frame, token);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { await _log.WriteAsync($"Live-экран: {ex.Message}"); }
            await Task.Delay(1000 / fps, token);
        }
    }

    private async Task ObsAsync(string requestType)
    {
        var obs = new ObsClient(_config.ObsPassword);
        var knownFiles = requestType == "SaveReplayBuffer" ? SnapshotObsFiles() : null;
        string? outputPath;
        try
        {
            outputPath = await obs.SendAsync(requestType);
        }
        catch (WebSocketException) when (File.Exists(_config.ObsPath))
        {
            Process.Start(new ProcessStartInfo(_config.ObsPath, "--minimize-to-tray --disable-shutdown-check")
            {
                WorkingDirectory = Path.GetDirectoryName(_config.ObsPath),
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Minimized
            });
            await Task.Delay(TimeSpan.FromSeconds(7));
            outputPath = await obs.SendAsync(requestType);
        }
        await SendJsonAsync(new { type = "obs_status", status = requestType switch
        {
            "StartStream" => "streaming",
            "StartRecord" => "recording",
            "StartReplayBuffer" => "replay_on",
            "StopReplayBuffer" => "replay_off",
            _ => "idle"
        }});
        if (requestType is "StopRecord" or "SaveReplayBuffer")
        {
            await Task.Delay(TimeSpan.FromSeconds(requestType == "StopRecord" ? 4 : 6));
            await UploadObsFileAsync(outputPath, knownFiles);
        }
    }

    private HashSet<string> SnapshotObsFiles()
    {
        var root = new DirectoryInfo(_config.ObsRecordPath);
        if (!root.Exists) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Select(file => file.FullName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    private async Task UploadObsFileAsync(string? outputPath, HashSet<string>? knownFiles)
    {
        var root = new DirectoryInfo(_config.ObsRecordPath);
        if (!root.Exists) throw new DirectoryNotFoundException("Папка записей OBS не найдена");
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { ".mp4", ".mkv", ".flv", ".mov" };
        FileInfo? file = null;
        if (!string.IsNullOrWhiteSpace(outputPath) && File.Exists(outputPath))
            file = new FileInfo(outputPath);
        file ??= root.EnumerateFiles("*", SearchOption.AllDirectories)
            .Where(value => extensions.Contains(value.Extension)
                            && value.Length > 100_000
                            && value.LastWriteTimeUtc > DateTime.UtcNow.AddMinutes(-10)
                            && (knownFiles == null || !knownFiles.Contains(value.FullName)))
            .OrderByDescending(value => value.LastWriteTimeUtc)
            .FirstOrDefault();
        file ??= throw new FileNotFoundException("Свежая запись OBS не найдена");

        using var upload = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{_config.Server}/api/pc/v2/agent/clip");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _config.AgentToken);
        request.Headers.Add("X-Filename", file.Name);
        await using var stream = file.OpenRead();
        request.Content = new StreamContent(stream);
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        using var response = await upload.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();
        await SendJsonAsync(new { type = "obs_status", status = $"clip_ready:{_config.UserId}_{file.Name}" });
    }

    private Task SendAckAsync(string id, bool ok, string? error) =>
        SendJsonAsync(new { type = "ack", id, status = ok ? "ok" : "error", error });

    private async Task SendJsonAsync(object payload)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, JsonOptions.Default);
        await SendAsync(bytes, WebSocketMessageType.Text, CancellationToken.None);
    }

    private Task SendBinaryAsync(byte[] payload, CancellationToken token) =>
        SendAsync(payload, WebSocketMessageType.Binary, token);

    private async Task SendAsync(byte[] payload, WebSocketMessageType type, CancellationToken token)
    {
        if (_socket?.State != WebSocketState.Open) return;
        await _sendLock.WaitAsync(token);
        try { await _socket.SendAsync(new ArraySegment<byte>(payload), type, true, token); }
        finally { _sendLock.Release(); }
    }
}

internal static class ScreenCapture
{
    public static byte[] Capture(int maxWidth, int quality)
    {
        var bounds = Screen.PrimaryScreen?.Bounds ?? throw new InvalidOperationException("Экран не найден");
        using var source = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format24bppRgb);
        using (var graphics = Graphics.FromImage(source))
            graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size);

        var width = Math.Min(maxWidth, source.Width);
        var height = (int)Math.Round(source.Height * (width / (double)source.Width));
        using var target = width == source.Width ? new Bitmap(source) : new Bitmap(source, width, height);
        using var output = new MemoryStream();
        var codec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        using var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, quality);
        target.Save(output, codec, parameters);
        return output.ToArray();
    }
}

internal static class NativeActions
{
    private const int WmSysCommand = 0x0112;
    private const int ScMonitorPower = 0xF170;
    private const int WmAppCommand = 0x0319;
    private const uint KeyUp = 0x0002;
    public const int ApVolumeMute = 8;
    public const int ApVolumeDown = 9;
    public const int ApVolumeUp = 10;
    public const int ApMute = ApVolumeMute;

    [DllImport("user32.dll")] public static extern bool LockWorkStation();
    [DllImport("user32.dll")] private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] private static extern void keybd_event(byte key, byte scan, uint flags, UIntPtr extra);

    public static void Start(string file, string args = "", bool hidden = false) => Process.Start(new ProcessStartInfo(file, args)
    {
        UseShellExecute = true,
        WindowStyle = hidden ? ProcessWindowStyle.Hidden : ProcessWindowStyle.Normal
    });

    public static void RecoverHiddenTaskManager()
    {
        var currentSession = Process.GetCurrentProcess().SessionId;
        foreach (var process in Process.GetProcessesByName("Taskmgr"))
        {
            try
            {
                if (process.SessionId == currentSession && process.MainWindowHandle == IntPtr.Zero)
                    process.Kill();
            }
            catch { }
            finally { process.Dispose(); }
        }
    }
    public static void MonitorOff() => SendMessage(new IntPtr(0xffff), WmSysCommand, new IntPtr(ScMonitorPower), new IntPtr(2));
    public static void AppCommand(int command) => SendMessage(new IntPtr(0xffff), WmAppCommand, IntPtr.Zero, new IntPtr(command << 16));
    public static void Key(byte key)
    {
        keybd_event(key, 0, 0, UIntPtr.Zero);
        keybd_event(key, 0, KeyUp, UIntPtr.Zero);
    }
    public static void Combo(byte first, byte second)
    {
        keybd_event(first, 0, 0, UIntPtr.Zero);
        keybd_event(second, 0, 0, UIntPtr.Zero);
        keybd_event(second, 0, KeyUp, UIntPtr.Zero);
        keybd_event(first, 0, KeyUp, UIntPtr.Zero);
    }
}

internal static class AudioController
{
    public static void SetMute(bool muted)
    {
        var endpoint = GetEndpoint();
        endpoint.SetMute(muted, Guid.Empty);
    }

    public static void ChangeVolume(float delta)
    {
        var endpoint = GetEndpoint();
        endpoint.GetMasterVolumeLevelScalar(out var current);
        endpoint.SetMasterVolumeLevelScalar(Math.Clamp(current + delta, 0f, 1f), Guid.Empty);
    }

    private static IAudioEndpointVolume GetEndpoint()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        Marshal.ThrowExceptionForHR(enumerator.GetDefaultAudioEndpoint(0, 1, out var device));
        var iid = typeof(IAudioEndpointVolume).GUID;
        Marshal.ThrowExceptionForHR(device.Activate(ref iid, 23, IntPtr.Zero, out var result));
        return (IAudioEndpointVolume)result;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object result);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, Guid context);
        [PreserveSig] int GetMute(out bool muted);
    }
}

internal sealed class ObsClient
{
    private readonly string _password;
    public ObsClient(string password) => _password = password;

    public async Task<string?> SendAsync(string requestType)
    {
        using var socket = new ClientWebSocket();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(
            requestType is "StopRecord" or "SaveReplayBuffer" ? 30 : 8));
        await socket.ConnectAsync(new Uri("ws://127.0.0.1:4455"), timeout.Token);
        var hello = await ReceiveJsonAsync(socket, timeout.Token);
        string? authentication = null;
        if (hello.RootElement.GetProperty("d").TryGetProperty("authentication", out var auth))
        {
            var salt = auth.GetProperty("salt").GetString() ?? "";
            var challenge = auth.GetProperty("challenge").GetString() ?? "";
            var secret = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(_password + salt)));
            authentication = Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(secret + challenge)));
        }
        await SendJsonAsync(socket, new { op = 1, d = new { rpcVersion = 1, authentication } }, timeout.Token);
        using var identified = await ReceiveJsonAsync(socket, timeout.Token);
        var requestId = Guid.NewGuid().ToString();
        await SendJsonAsync(socket, new
        {
            op = 6,
            d = new { requestType, requestId, requestData = new { } }
        }, timeout.Token);
        while (true)
        {
            using var response = await ReceiveJsonAsync(socket, timeout.Token);
            var root = response.RootElement;
            if (!root.TryGetProperty("op", out var op) || op.GetInt32() != 7) continue;
            var data = root.GetProperty("d");
            if (!data.TryGetProperty("requestId", out var responseId)
                || responseId.GetString() != requestId) continue;
            var result = data.GetProperty("requestStatus");
            if (!result.GetProperty("result").GetBoolean())
                throw new InvalidOperationException(result.GetProperty("comment").GetString() ?? "Ошибка OBS");
            if (data.TryGetProperty("responseData", out var responseData)
                && responseData.TryGetProperty("outputPath", out var outputPath))
                return outputPath.GetString();
            return null;
        }
    }

    private static async Task SendJsonAsync(ClientWebSocket socket, object value, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions.Default);
        await socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, token);
    }

    private static async Task<JsonDocument> ReceiveJsonAsync(ClientWebSocket socket, CancellationToken token)
    {
        var buffer = new byte[64 * 1024];
        using var stream = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), token);
            stream.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonDocument.Parse(stream.ToArray());
    }
}
