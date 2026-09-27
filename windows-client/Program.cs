using System.Diagnostics;
using System.Collections.Concurrent;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;
using NAudio.Wave;

namespace EchoRemote.WinForms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly WebView2 peerView = new() { Visible = true, Width = 2, Height = 2, Location = new Point(-10, -10) };
    private readonly TextBox peerIdBox = new() { Width = 220, PlaceholderText = "空欄ならランダムID" };
    private readonly TextBox passwordBox = new() { Width = 220, UseSystemPasswordChar = true, PlaceholderText = "8文字以上を推奨" };
    private readonly TextBox nameBox = new() { Width = 220, Text = Environment.MachineName };
    private readonly ComboBox screenBox = new() { Width = 290, DropDownStyle = ComboBoxStyle.DropDownList };
    private readonly CheckBox acceptBox = new() { Text = "パスワード認証後、ブラウザからの接続を自動許可", AutoSize = true, Checked = true };
    private readonly Label idValue = new() { AutoSize = true, Font = new Font("Segoe UI", 11, FontStyle.Bold), Text = "未接続" };
    private readonly Label statusValue = new() { AutoSize = true, Text = "起動してください" };
    private readonly Button startButton = new() { Text = "PeerJSを起動", AutoSize = true };
    private readonly Button stopButton = new() { Text = "停止", AutoSize = true, Enabled = false };
    private readonly Button refreshButton = new() { Text = "画面一覧を更新", AutoSize = true };
    private readonly Button exitButton = new() { Text = "終了", AutoSize = true };
    private readonly System.Windows.Forms.Timer frameTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer audioSendTimer = new() { Interval = 20 };
    private readonly string configPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "EchoRemote", "settings.json");
    private readonly object screenLock = new();
    private bool pageReady;
    private volatile bool audioBroadcastEnabled;
    private volatile WasapiLoopbackCapture? audioCapture;
    private volatile int audioSampleRate = 48000;
    private long audioGeneration;
    private string? browserPeerId;
    private PeerData? currentData;
    private DisplaySetting currentSetting = new("clone", true, 1280);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> chunkAcks = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<bool>> fileRejects = new();
    private readonly ConcurrentQueue<byte[]> audioPackets = new();

    public MainForm()
    {
        Text = "Echo Remote · Windows Client";
        Width = 710; Height = 530; MinimumSize = new Size(660, 490);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9.5f);
        BuildUi();
        LoadConfig();
        RefreshDisplays();
        startButton.Click += async (_, _) => await StartPeerAsync();
        stopButton.Click += (_, _) => StopPeer();
        refreshButton.Click += (_, _) => RefreshDisplays();
        exitButton.Click += (_, _) => Close();
        frameTimer.Tick += (_, _) => CaptureFrame();
        audioSendTimer.Tick += (_, _) => SendAudioPackets();
        FormClosing += (_, _) => StopPeer();
        Shown += async (_, _) => await InitializePeerViewAsync();
    }

    private void BuildUi()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(24), ColumnCount = 2, RowCount = 8 };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 185)); root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        for (var i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = new Label { Text = "Echo Remote · Windows Host", Font = new Font("Segoe UI", 17, FontStyle.Bold), AutoSize = true, Dock = DockStyle.Fill };
        root.Controls.Add(title, 0, 0); root.SetColumnSpan(title, 2);
        AddRow(root, 1, "PCのPeerJSコード", peerIdBox);
        AddRow(root, 2, "接続パスワード", passwordBox);
        AddRow(root, 3, "表示名", nameBox);
        AddRow(root, 4, "配信する画面", screenBox);
        root.Controls.Add(refreshButton, 1, 5);
        root.Controls.Add(acceptBox, 1, 6);
        var bottom = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = true, AutoSize = true, Padding = new Padding(0, 8, 0, 0) };
        bottom.Controls.Add(startButton); bottom.Controls.Add(stopButton); bottom.Controls.Add(exitButton);
        root.Controls.Add(bottom, 0, 7);
        var status = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, Padding = new Padding(4, 7, 0, 0) };
        status.Controls.Add(new Label { Text = "PeerJSコード（ブラウザ側に入力）", ForeColor = Color.DimGray, AutoSize = true }); status.Controls.Add(idValue); status.Controls.Add(statusValue);
        root.Controls.Add(status, 1, 7);
        Controls.Add(root); Controls.Add(peerView);
        var note = new Label { Text = "接続中は画面を共有し、ブラウザからのマウス・文字入力を受け付けます。パスワードを安全に保管してください。", Dock = DockStyle.Bottom, Height = 34, ForeColor = Color.DimGray, Padding = new Padding(24, 0, 24, 6) };
        Controls.Add(note);
    }

    private static void AddRow(TableLayoutPanel panel, int row, string label, Control control)
    {
        var l = new Label { Text = label, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft };
        control.Anchor = AnchorStyles.Left; panel.Controls.Add(l, 0, row); panel.Controls.Add(control, 1, row);
    }

    private async Task InitializePeerViewAsync()
    {
        try
        {
            await peerView.EnsureCoreWebView2Async();
            peerView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            peerView.CoreWebView2.Settings.AreDevToolsEnabled = false;
            peerView.CoreWebView2.Settings.IsStatusBarEnabled = false;
            peerView.CoreWebView2.WebMessageReceived += (_, e) => HandleWebMessage(e.TryGetWebMessageAsString());
            peerView.CoreWebView2.NavigateToString(PeerHostHtml);
            UpdateStatus("PeerJSライブラリとWebRTCの初期化中…");
        }
        catch (Exception ex)
        {
            UpdateStatus("WebView2 Runtimeが必要です: " + ex.Message);
            MessageBox.Show("Microsoft Edge WebView2 Runtimeをインストールしてください。\nhttps://developer.microsoft.com/microsoft-edge/webview2/", "WebView2が必要です", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private async Task StartPeerAsync()
    {
        var password = passwordBox.Text;
        if (password.Length < 8)
        {
            MessageBox.Show("接続パスワードは8文字以上を推奨します。短いパスワードは使用前に変更してください。", "パスワード", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            passwordBox.Focus(); return;
        }
        if (!pageReady) { UpdateStatus("PeerJS初期化を待っています"); return; }
        SaveConfig();
        startButton.Enabled = false;
        await SendToPeerPageAsync(new { cmd = "start", peerId = peerIdBox.Text.Trim(), password, name = nameBox.Text.Trim(), accept = acceptBox.Checked });
    }

    private Task SendToPeerPageAsync(object message)
    {
        if (peerView.IsDisposed || !peerView.IsHandleCreated) return Task.CompletedTask;
        if (!peerView.InvokeRequired) return SendToPeerPageOnUiAsync(message);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            peerView.BeginInvoke(new Action(async () =>
            {
                try { await SendToPeerPageOnUiAsync(message); completion.TrySetResult(true); }
                catch (Exception ex) { completion.TrySetException(ex); }
            }));
        }
        catch (Exception ex) { completion.TrySetException(ex); }
        return completion.Task;
    }

    private async Task SendToPeerPageOnUiAsync(object message)
    {
        if (peerView.CoreWebView2 is null) return;
        await peerView.CoreWebView2.ExecuteScriptAsync($"window.hostMessage({JsonSerializer.Serialize(JsonSerializer.Serialize(message))})");
    }

    private void HandleWebMessage(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            var root = doc.RootElement;
            var type = root.TryGetProperty("type", out var t) ? t.GetString() : "";
            switch (type)
            {
                case "status":
                    var statusText = root.GetProperty("text").GetString() ?? "";
                    UpdateStatus(statusText);
                    if (statusText.StartsWith("認証済み", StringComparison.Ordinal) && root.TryGetProperty("peerId", out var id))
                    {
                        browserPeerId = id.GetString();
                    }
                    if (root.TryGetProperty("running", out var running))
                    {
                        startButton.Enabled = !running.GetBoolean(); stopButton.Enabled = running.GetBoolean();
                    }
                    break;
                case "peer-id":
                    var peerId = root.GetProperty("id").GetString() ?? "";
                    idValue.Text = peerId;
                    peerIdBox.Text = peerId;
                    SaveConfig();
                    break;
                case "ready":
                    pageReady = true;
                    UpdateStatus("WebView2のPeerJSホスト準備完了");
                    break;
                case "auth-ok":
                    currentData = new PeerData(SendJsonFromHostAsync);
                    break;
                case "auth-failed":
                    browserPeerId = null;
                    UpdateStatus("パスワードが一致しない接続を拒否しました");
                    break;
                case "peer-connection":
                    browserPeerId = root.GetProperty("peerId").GetString();
                    currentData = new PeerData(SendJsonFromHostAsync);
                    break;
                case "data":
                    browserPeerId = root.GetProperty("peerId").GetString();
                    ProcessBrowserData(root.GetProperty("payload"));
                    break;
                case "call":
                    browserPeerId = root.GetProperty("peerId").GetString();
                    _ = AnswerCallAsync(root.GetProperty("callId").GetString() ?? "");
                    break;
                case "call-closed":
                    UpdateStatus("映像接続終了");
                    break;
                case "disconnected":
                    currentData = null; browserPeerId = null;
                    frameTimer.Stop();
                    StopAudio();
                    UpdateStatus("ブラウザが切断しました");
                    break;
                case "error":
                    UpdateStatus(root.GetProperty("text").GetString() ?? "PeerJSエラー");
                    startButton.Enabled = true;
                    break;
            }
        }
        catch (Exception ex) { Debug.WriteLine("Host message error: " + ex.Message); }
    }

    private async Task SendJsonFromHostAsync(object payload) => await SendToPeerPageAsync(new { cmd = "send", peerId = browserPeerId, payload });
    private async Task AnswerCallAsync(string callId) => await SendToPeerPageAsync(new { cmd = "answer", callId });

    private void ProcessBrowserData(JsonElement data)
    {
        var type = data.TryGetProperty("type", out var t) ? t.GetString() : "";
        switch (type)
        {
            case "auth":
                var supplied = data.TryGetProperty("password", out var p) ? p.GetString() ?? "" : "";
                var saved = passwordBox.Text;
                var valid = CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(supplied)), SHA256.HashData(Encoding.UTF8.GetBytes(saved)));
                if (valid && acceptBox.Checked)
                {
                    UpdateStatus("認証済み · ブラウザ接続中: " + (browserPeerId ?? "peer"));
                    _ = SendJsonFromHostAsync(new { type = "auth-ok", name = nameBox.Text.Trim() });
                }
                else
                {
                    UpdateStatus(valid ? "接続は拒否されました。自動許可がオフです" : "パスワードが一致しない接続を拒否しました");
                    _ = SendJsonFromHostAsync(new { type = "auth-failed" });
                }
                break;
            case "settings":
                var mode = data.TryGetProperty("mode", out var m) ? m.GetString() ?? "clone" : "clone";
                var fit = data.TryGetProperty("fit", out var f) && f.GetBoolean();
                var resolution = data.TryGetProperty("resolution", out var r) && r.ValueKind == JsonValueKind.Number
                    ? Math.Clamp(r.GetInt32(), 640, 3840)
                    : currentSetting.Resolution;
                currentSetting = new DisplaySetting(mode, fit, resolution);
                SaveConfig();
                ApplyDisplayMode(mode);
                break;
            case "request-files":
                BeginInvoke(new Action(RequestFiles));
                break;
            case "file-chunk-ack":
                if (data.TryGetProperty("id", out var ackId) && data.TryGetProperty("index", out var ackIndex) && chunkAcks.TryGetValue($"{ackId.GetString()}:{ackIndex.GetInt32()}", out var ack)) ack.TrySetResult(true);
                break;
            case "file-reject":
                if (data.TryGetProperty("id", out var rejectId) && fileRejects.TryGetValue(rejectId.GetString() ?? "", out var reject)) reject.TrySetResult(true);
                break;
            case "pointer":
                ApplyPointer(data);
                break;
            case "text":
                if (data.TryGetProperty("text", out var text)) SendUnicodeText(text.GetString() ?? "");
                break;
            case "key":
                if (data.TryGetProperty("key", out var key)) SendVirtualKey(key.GetInt32(), data.TryGetProperty("down", out var down) && down.GetBoolean());
                break;
            case "audio":
                var audioOn = data.TryGetProperty("enabled", out var audioEnabled) && audioEnabled.GetBoolean();
                BeginInvoke(new Action(() => { if (audioOn) StartAudio(); else StopAudio(); }));
                break;
        }
    }

    private void StartAudio()
    {
        if (audioCapture is not null) return;
        try
        {
            var capture = new WasapiLoopbackCapture();
            var generation = Interlocked.Increment(ref audioGeneration);
            audioCapture = capture;
            audioSampleRate = capture.WaveFormat.SampleRate;
            audioBroadcastEnabled = true;
            capture.DataAvailable += (sender, e) =>
            {
                if (!audioBroadcastEnabled || generation != Interlocked.Read(ref audioGeneration) || e.BytesRecorded <= 0) return;
                try
                {
                    var pcm = ToStereoPcm16(e.Buffer, e.BytesRecorded, capture.WaveFormat, out var channels);
                    if (pcm.Length == 0) return;
                    audioPackets.Enqueue(pcm);
                    while (audioPackets.Count > 20) audioPackets.TryDequeue(out _);
                }
                catch (Exception ex) { Debug.WriteLine("Audio frame: " + ex.Message); }
            };
            capture.RecordingStopped += (sender, e) =>
            {
                if (generation == Interlocked.Read(ref audioGeneration))
                {
                    audioBroadcastEnabled = false;
                    if (InvokeRequired)
                    {
                        try { BeginInvoke(new Action(() => audioSendTimer.Stop())); } catch { }
                    }
                    else audioSendTimer.Stop();
                    while (audioPackets.TryDequeue(out _)) { }
                    if (ReferenceEquals(audioCapture, capture)) audioCapture = null;
                }
                capture.Dispose();
                if (e.Exception is not null && generation == Interlocked.Read(ref audioGeneration))
                {
                    currentData?.Send(new { type = "audio-state", enabled = false, message = "Windows音声の取得を停止しました" });
                    UpdateStatus("Windows音声取得エラー: " + e.Exception.Message);
                }
            };
            capture.StartRecording();
            while (audioPackets.TryDequeue(out _)) { }
            audioSendTimer.Start();
            currentData?.Send(new { type = "audio-state", enabled = true });
            UpdateStatus("Windowsシステム音声をEchoへ配信中");
        }
        catch (Exception ex)
        {
            audioBroadcastEnabled = false;
            audioCapture?.Dispose(); audioCapture = null;
            currentData?.Send(new { type = "audio-state", enabled = false, message = "Windows音声を開始できませんでした" });
            UpdateStatus("Windows音声を開始できません: " + ex.Message);
        }
    }

    private void StopAudio()
    {
        audioBroadcastEnabled = false;
        Interlocked.Increment(ref audioGeneration);
        audioSendTimer.Stop();
        while (audioPackets.TryDequeue(out _)) { }
        var capture = audioCapture;
        audioCapture = null;
        if (capture is not null)
        {
            try { capture.StopRecording(); }
            catch { capture.Dispose(); }
        }
        currentData?.Send(new { type = "audio-state", enabled = false });
    }

    private void SendAudioPackets()
    {
        var sink = currentData;
        if (!audioBroadcastEnabled || sink is null || browserPeerId is null) return;
        var parts = new List<byte[]>(8);
        var total = 0;
        while (audioPackets.TryDequeue(out var packet) && total + packet.Length <= 24 * 1024)
        {
            parts.Add(packet);
            total += packet.Length;
        }
        if (total == 0) return;
        var merged = new byte[total];
        var offset = 0;
        foreach (var part in parts) { Buffer.BlockCopy(part, 0, merged, offset, part.Length); offset += part.Length; }
        _ = sink.Send(new { type = "audio-data", sampleRate = audioSampleRate, channels = 2, data = Convert.ToBase64String(merged) });
    }

    private static byte[] ToStereoPcm16(byte[] source, int length, WaveFormat format, out int outputChannels)
    {
        var inputChannels = Math.Max(1, format.Channels);
        outputChannels = 2;
        var frameBytes = Math.Max(1, format.BlockAlign);
        var frames = length / frameBytes;
        if (frames == 0) return Array.Empty<byte>();
        var bytesPerSample = Math.Max(1, format.BitsPerSample / 8);
        var output = new byte[frames * outputChannels * 2];
        var floatSamples = format.Encoding == WaveFormatEncoding.IeeeFloat ||
            (format is WaveFormatExtensible extensible && extensible.SubFormat == IeeeFloatSubformat);
        for (var frame = 0; frame < frames; frame++)
        {
            var left = 0.0; var right = 0.0; var leftCount = 0; var rightCount = 0;
            for (var channel = 0; channel < inputChannels; channel++)
            {
                var sample = ReadLoopbackSample(source, frame * frameBytes + channel * bytesPerSample, format.BitsPerSample, floatSamples);
                if (inputChannels == 1 || channel % 2 == 0) { left += sample; leftCount++; }
                if (inputChannels == 1 || channel % 2 == 1) { right += sample; rightCount++; }
            }
            var baseIndex = frame * outputChannels * 2;
            WritePcm16(output, baseIndex, left / Math.Max(1, leftCount));
            if (outputChannels == 2) WritePcm16(output, baseIndex + 2, right / Math.Max(1, rightCount));
        }
        return output;
    }

    private static double ReadLoopbackSample(byte[] data, int offset, int bits, bool floatSamples)
    {
        if (floatSamples && bits == 32) return Math.Clamp(BitConverter.ToSingle(data, offset), -1f, 1f);
        return bits switch
        {
            8 => (data[offset] - 128) / 128.0,
            16 => BitConverter.ToInt16(data, offset) / 32768.0,
            24 => ((data[offset] | data[offset + 1] << 8 | data[offset + 2] << 16) << 8 >> 8) / 8388608.0,
            32 => BitConverter.ToInt32(data, offset) / 2147483648.0,
            _ => 0.0
        };
    }

    private static void WritePcm16(byte[] output, int offset, double sample)
    {
        var value = (short)Math.Round(Math.Clamp(sample, -1.0, 1.0) * 32767.0);
        output[offset] = (byte)(value & 0xff); output[offset + 1] = (byte)((value >> 8) & 0xff);
    }

    private static readonly Guid IeeeFloatSubformat = new("00000003-0000-0010-8000-00aa00389b71");

    private void ApplyPointer(JsonElement data)
    {
        if (screenBox.SelectedItem is not ScreenChoice choice) return;
        var screen = choice.Screen;
        var action = data.GetProperty("action").GetString();
        var nx = Math.Clamp(data.GetProperty("x").GetDouble(), 0, 1);
        var ny = Math.Clamp(data.GetProperty("y").GetDouble(), 0, 1);
        var x = screen.Bounds.Left + (int)Math.Round(nx * Math.Max(0, screen.Bounds.Width - 1));
        var y = screen.Bounds.Top + (int)Math.Round(ny * Math.Max(0, screen.Bounds.Height - 1));
        SetCursorPos(x, y);
        if (action == "down") MouseEvent(MouseFlags(data, true));
        else if (action == "up") MouseEvent(MouseFlags(data, false));
    }

    private static uint MouseFlags(JsonElement data, bool down)
    {
        var button = data.TryGetProperty("button", out var b) ? b.GetInt32() : 0;
        return button switch { 2 => down ? 0x0008u : 0x0010u, 1 => down ? 0x0020u : 0x0040u, _ => down ? 0x0002u : 0x0004u };
    }

    private static void MouseEvent(uint flags)
    {
        var input = new INPUT { type = 0, U = new InputUnion { mi = new MOUSEINPUT { dwFlags = flags } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendVirtualKey(int key, bool down)
    {
        var input = new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wVk = (ushort)key, dwFlags = down ? 0u : 0x0002u } } };
        SendInput(1, new[] { input }, Marshal.SizeOf<INPUT>());
    }

    private static void SendUnicodeText(string text)
    {
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value == '\n') { SendVirtualKey(0x0D, true); SendVirtualKey(0x0D, false); continue; }
            if (rune.Value == '\t') { SendVirtualKey(0x09, true); SendVirtualKey(0x09, false); continue; }
            if (rune.Value == '\b') { SendVirtualKey(0x08, true); SendVirtualKey(0x08, false); continue; }
            foreach (var unit in rune.ToString())
            {
                var inputs = new[]
                {
                    new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = 0x0004 } } },
                    new INPUT { type = 1, U = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = 0x0004 | 0x0002 } } }
                };
                SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
            }
        }
    }

    private void RequestFiles()
    {
        using var dlg = new OpenFileDialog { Title = "Echo Showへ送るファイルを選択", Filter = "すべてのファイル (*.*)|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != DialogResult.OK) return;
        _ = SendFilesAsync(dlg.FileNames);
    }

    private async Task SendFilesAsync(string[] paths)
    {
        const int chunkSize = 24 * 1024;
        foreach (var path in paths)
        {
            string? currentId = null;
            try
            {
                var info = new FileInfo(path);
                var id = Guid.NewGuid().ToString("N");
                currentId = id;
                var reject = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                fileRejects[id] = reject;
                var mime = GetMimeType(info.Extension);
                await SendJsonFromHostAsync(new { type = "file-start", id, name = info.Name, size = info.Length, mime });
                if (await Task.WhenAny(reject.Task, Task.Delay(250)) == reject.Task)
                {
                    UpdateStatus($"受信端末がファイルを拒否: {info.Name}"); fileRejects.TryRemove(id, out _); continue;
                }
                await using var stream = File.OpenRead(path);
                var buffer = new byte[chunkSize]; var index = 0; int read;
                while ((read = await stream.ReadAsync(buffer.AsMemory(0, buffer.Length))) > 0)
                {
                    var base64 = Convert.ToBase64String(buffer, 0, read);
                    var key = $"{id}:{index}";
                    var ack = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    chunkAcks[key] = ack;
                    await SendJsonFromHostAsync(new { type = "file-chunk", id, index, data = base64 });
                    var done = await Task.WhenAny(ack.Task, reject.Task, Task.Delay(TimeSpan.FromSeconds(30)));
                    chunkAcks.TryRemove(key, out _);
                    if (done == reject.Task) throw new OperationCanceledException("受信端末がファイルを拒否しました");
                    if (done != ack.Task) throw new TimeoutException("ファイル受信確認がタイムアウトしました");
                    index++;
                }
                await SendJsonFromHostAsync(new { type = "file-end", id });
                UpdateStatus($"送信完了: {info.Name}");
            }
            catch (Exception ex) { await SendJsonFromHostAsync(new { type = "file-error", message = Path.GetFileName(path) + ": " + ex.Message }); }
            finally { if (currentId is not null) fileRejects.TryRemove(currentId, out _); }
        }
    }

    private static string GetMimeType(string ext) => ext.ToLowerInvariant() switch
    {
        ".txt" => "text/plain", ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" or ".jpeg" => "image/jpeg", ".gif" => "image/gif", ".webp" => "image/webp",
        ".mp4" => "video/mp4", ".webm" => "video/webm", ".mp3" => "audio/mpeg", ".wav" => "audio/wav", ".m4a" => "audio/mp4", ".ogg" => "audio/ogg", _ => "application/octet-stream"
    };

    private void CaptureFrame()
    {
        if (!pageReady) return;
        try
        {
            Screen target;
            lock (screenLock) target = ResolveScreen();
            var bounds = target.Bounds;
            if (bounds.Width <= 0 || bounds.Height <= 0) return;
            var maxWidth = Math.Clamp(currentSetting.Resolution, 640, 3840);
            var scale = Math.Min(1.0, maxWidth / (double)bounds.Width);
            var width = Math.Max(1, (int)(bounds.Width * scale));
            var height = Math.Max(1, (int)(bounds.Height * scale));
            using var raw = new Bitmap(bounds.Width, bounds.Height);
            using (var source = Graphics.FromImage(raw))
            {
                source.CopyFromScreen(bounds.Left, bounds.Top, 0, 0, new Size(bounds.Width, bounds.Height), CopyPixelOperation.SourceCopy);
            }
            using var bitmap = new Bitmap(width, height);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBilinear;
                graphics.DrawImage(raw, new Rectangle(0, 0, width, height));
            }
            using var memory = new MemoryStream();
            var encoder = ImageCodecInfo.GetImageEncoders().First(c => c.MimeType == "image/jpeg");
            using var parameters = new EncoderParameters(1); parameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, 56L);
            bitmap.Save(memory, encoder, parameters);
            var b64 = Convert.ToBase64String(memory.ToArray());
            peerView.CoreWebView2?.PostWebMessageAsString("frame:" + b64);
        }
        catch (Exception ex) { Debug.WriteLine("Screen capture: " + ex.Message); }
    }

    private Screen ResolveScreen()
    {
        if (screenBox.SelectedItem is ScreenChoice selected) return selected.Screen;
        var screens = Screen.AllScreens;
        if (currentSetting.Mode == "extend") return screens.FirstOrDefault(s => !s.Primary) ?? Screen.PrimaryScreen ?? screens[0];
        return Screen.PrimaryScreen ?? screens[0];
    }

    private void ApplyDisplayMode(string mode)
    {
        try
        {
            var exe = Path.Combine(Environment.SystemDirectory, "DisplaySwitch.exe");
            if (File.Exists(exe))
            {
                using var proc = Process.Start(new ProcessStartInfo(exe, mode == "extend" ? "/extend" : "/clone") { UseShellExecute = false, CreateNoWindow = true });
                proc?.WaitForExit(2500);
            }
            RefreshDisplays();
            var hasSecondary = Screen.AllScreens.Any(s => !s.Primary);
            if (mode == "extend" && !hasSecondary)
            {
                UpdateStatus("拡張モードを要求しましたが、Windowsに第2画面がありません。Echo Showは仮想モニターではありません");
                currentData?.Send(new { type = "notice", message = "拡張モードにはWindows側に第2ディスプレイが必要です。現在はメイン画面を送信しています。" });
                return;
            }
            UpdateStatus(mode == "extend" ? "Windows拡張モード · 第2画面を配信" : "Windows複製モード · メイン画面を配信");
        }
        catch (Exception ex)
        {
            UpdateStatus("画面モードを変更できません: " + ex.Message);
            currentData?.Send(new { type = "notice", message = "画面モードを変更できませんでした。Windowsの画面設定を確認してください。" });
        }
    }

    private void RefreshDisplays()
    {
        var selected = screenBox.SelectedItem as ScreenChoice;
        screenBox.Items.Clear();
        foreach (var s in Screen.AllScreens) screenBox.Items.Add(new ScreenChoice(s, $"{(s.Primary ? "メイン" : "拡張")}: {s.DeviceName}  ({s.Bounds.Width}×{s.Bounds.Height})"));
        var wanted = currentSetting.Mode == "extend" ? Screen.AllScreens.FirstOrDefault(s => !s.Primary) : Screen.PrimaryScreen;
        var idx = 0;
        if (wanted is not null) for (var i = 0; i < screenBox.Items.Count; i++) if (screenBox.Items[i] is ScreenChoice candidate && candidate.Screen.DeviceName == wanted.DeviceName) { idx = i; break; }
        if (screenBox.Items.Count > 0) screenBox.SelectedIndex = idx;
        statusValue.Text = $"検出ディスプレイ: {Screen.AllScreens.Length}台";
    }

    private void StopPeer()
    {
        if (frameTimer.Enabled) frameTimer.Stop();
        StopAudio();
        if (pageReady) _ = SendToPeerPageAsync(new { cmd = "stop" });
        currentData = null; browserPeerId = null;
        startButton.Enabled = true; stopButton.Enabled = false;
        UpdateStatus("停止しました");
    }

    private void UpdateStatus(string text)
    {
        if (InvokeRequired) { BeginInvoke(new Action(() => UpdateStatus(text))); return; }
        statusValue.Text = text;
        if (text.StartsWith("認証済み", StringComparison.Ordinal)) frameTimer.Start();
        if (text.Contains("拒否", StringComparison.Ordinal) || text.Contains("終了", StringComparison.Ordinal)) frameTimer.Stop();
    }

    private void SaveConfig()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(configPath)!);
            File.WriteAllText(configPath, JsonSerializer.Serialize(new HostConfig(peerIdBox.Text, passwordBox.Text, nameBox.Text, acceptBox.Checked, currentSetting)));
        }
        catch (Exception ex) { Debug.WriteLine(ex); }
    }

    private void LoadConfig()
    {
        try
        {
            if (!File.Exists(configPath)) return;
            var c = JsonSerializer.Deserialize<HostConfig>(File.ReadAllText(configPath));
            if (c is null) return;
            peerIdBox.Text = c.PeerId; passwordBox.Text = c.Password; nameBox.Text = c.Name; acceptBox.Checked = c.Accept; currentSetting = c.Display ?? currentSetting;
            if (currentSetting.Resolution < 640 || currentSetting.Resolution > 3840) currentSetting = currentSetting with { Resolution = 1280 };
        }
        catch { }
    }

    private sealed record DisplaySetting(string Mode, bool Fit, int Resolution = 1280);
    private sealed record HostConfig(string PeerId, string Password, string Name, bool Accept, DisplaySetting Display);
    private sealed record ScreenChoice(Screen Screen, string Label) { public override string ToString() => Label; }
    private sealed record PeerData(Func<object, Task> Send);
    private const string PeerHostHtml = """
    <!doctype html><html><head><meta charset="utf-8"><script src="https://cdn.jsdelivr.net/npm/peerjs@1.5.5/dist/peerjs.min.js"></script></head>
    <body style="margin:0;background:#000"><canvas id="screen"></canvas><script>
    (()=>{const canvas=document.getElementById('screen'),ctx=canvas.getContext('2d',{alpha:false});let peer=null,password='',accept=false,connections=new Map(),calls=new Map(),stream=canvas.captureStream(5);window.hostMessage=(json)=>{const m=JSON.parse(json);if(m.cmd==='start')start(m);else if(m.cmd==='stop')stop();else if(m.cmd==='send'){const p=m.payload;const c=connections.get(m.peerId);if(c&&c.open&&c._echoAuthenticated)c.send(p)}else if(m.cmd==='answer'){const call=calls.get(m.callId);if(call){call.answer(stream);call.on('close',()=>chrome.webview.postMessage(JSON.stringify({type:'call-closed'})))}}};
    function emit(o){chrome.webview.postMessage(JSON.stringify(o))}function start(m){stop();password=m.password;accept=m.accept;try{const options={host:'0.peerjs.com',port:443,path:'/',secure:true};peer=m.peerId?new Peer(m.peerId,options):new Peer(undefined,options);peer.on('open',id=>{emit({type:'peer-id',id});emit({type:'status',text:'PeerJS接続済み。Echo Showから接続できます',peerId:id,running:true})});peer.on('connection',c=>{connections.set(c.peer,c);emit({type:'peer-connection',peerId:c.peer});c.on('data',d=>{if(d&&d.type==='auth'){if(accept&&d.password===password){c._echoAuthenticated=true;c.send({type:'auth-ok',name:m.name});emit({type:'status',text:'認証済み · ブラウザ接続中: '+c.peer,peerId:c.peer,running:true})}else{c._echoAuthenticated=false;c.send({type:'auth-failed'});emit({type:'auth-failed'});setTimeout(()=>{try{c.close()}catch{}},200)}}else if(c._echoAuthenticated){emit({type:'data',peerId:c.peer,payload:d})}else{try{c.close()}catch{}}});c.on('close',()=>{connections.delete(c.peer);emit({type:'disconnected'})});c.on('error',e=>emit({type:'error',text:e.message||'DataConnection error'}))});peer.on('call',call=>{const c=connections.get(call.peer);if(!c||!c._echoAuthenticated){try{call.close()}catch{}return}calls.set(call.connectionId||call.peer,call);emit({type:'call',peerId:call.peer,callId:call.connectionId||call.peer});call.on('close',()=>{calls.delete(call.connectionId||call.peer);emit({type:'call-closed'})})});peer.on('error',e=>emit({type:'error',text:e.type||e.message||'PeerJS error'}));peer.on('disconnected',()=>emit({type:'status',text:'PeerJS signalingが切断されました',running:false}));}catch(e){emit({type:'error',text:e.message||String(e)})}}
    function stop(){for(const c of connections.values())try{c.close()}catch{}for(const c of calls.values())try{c.close()}catch{}connections.clear();calls.clear();if(peer){try{peer.destroy()}catch{}peer=null}emit({type:'status',text:'停止しました',running:false})}
    chrome.webview.addEventListener('message',e=>{if(typeof e.data==='string'&&e.data.startsWith('frame:')){const im=new Image();im.onload=()=>{if(canvas.width!==im.naturalWidth||canvas.height!==im.naturalHeight){canvas.width=im.naturalWidth;canvas.height=im.naturalHeight}ctx.drawImage(im,0,0)};im.src='data:image/jpeg;base64,'+e.data.slice(6)}});emit({type:'ready'});
    })();</script></body></html>
""";

    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion U; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT { public int dx,dy; public uint mouseData,dwFlags,time; public IntPtr dwExtraInfo; }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT { public ushort wVk,wScan; public uint dwFlags,time; public IntPtr dwExtraInfo; }
    [DllImport("user32.dll", SetLastError=true)] private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);
    [DllImport("user32.dll", SetLastError=true)] private static extern bool SetCursorPos(int x,int y);
}
