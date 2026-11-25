using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using RemoteGuard.Host.Capture;
using RemoteGuard.Host.Security;

namespace RemoteGuard.Host.Network;

public sealed class ConnectionApprovalRequest
{
    public string DeviceId { get; init; } = string.Empty;
    public string DeviceName { get; init; } = string.Empty;
    public string ClientIp { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public bool IsNewDevice { get; init; }
}

public sealed class RemoteWebSocketServer : IDisposable
{
    private readonly SecurityStore _store;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly ConcurrentDictionary<string, WebSocket> _activeSessions = new();

    public bool IsRunning { get; private set; }
    public int ActiveClientCount => _activeSessions.Count;

    public event Action<string>? StatusChanged;
    public event Action<int>? ActiveClientsChanged;
    public Func<ConnectionApprovalRequest, Task<bool>>? OnApprovalRequired { get; set; }

    public RemoteWebSocketServer(SecurityStore store)
    {
        _store = store;
    }

    public void Start()
    {
        if (IsRunning) return;
        _cts = new CancellationTokenSource();
        int port = _store.Config.Port;

        _listener = new HttpListener();
        try
        {
            _listener.Prefixes.Add($"http://+:{port}/");
            _listener.Start();
        }
        catch
        {
            // Fallback if URLACL/admin isn't available
            _listener = new HttpListener();
            _listener.Prefixes.Add($"http://localhost:{port}/");
            _listener.Start();
        }

        IsRunning = true;
        StatusChanged?.Invoke($"Listening on port {port}");
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Stop()
    {
        if (!IsRunning) return;
        IsRunning = false;
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); _listener?.Close(); } catch { }
        foreach (var kv in _activeSessions)
        {
            try { kv.Value.Abort(); } catch { }
        }
        _activeSessions.Clear();
        ActiveClientsChanged?.Invoke(0);
        StatusChanged?.Invoke("Stopped");
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && _listener != null && _listener.IsListening)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch
            {
                if (ct.IsCancellationRequested) break;
                continue;
            }

            _ = Task.Run(() => HandleHttpContextAsync(ctx, ct), ct);
        }
    }

    private static string ExtractRealClientIp(HttpListenerRequest req)
    {
        // Cloudflare Tunnel sends real client IP in CF-Connecting-IP
        var cfIp = req.Headers["CF-Connecting-IP"];
        if (!string.IsNullOrWhiteSpace(cfIp)) return cfIp.Trim();

        var xff = req.Headers["X-Forwarded-For"];
        if (!string.IsNullOrWhiteSpace(xff))
            return xff.Split(',')[0].Trim();

        var remote = req.RemoteEndPoint?.Address?.ToString() ?? "unknown";
        if (remote == "::1") return "127.0.0.1";
        if (remote.StartsWith("::ffff:")) return remote[7..];
        return remote;
    }

    private async Task HandleHttpContextAsync(HttpListenerContext ctx, CancellationToken ct)
    {
        string path = ctx.Request.Url?.AbsolutePath ?? "/";

        if (!ctx.Request.IsWebSocketRequest)
        {
            // Simple health / info endpoint
            ctx.Response.ContentType = "application/json";
            var info = JsonSerializer.Serialize(new
            {
                service = "RemoteGuard",
                hostName = _store.Config.HostName,
                status = "online",
                locked = DesktopCaptureAndInput.IsLockScreenActive()
            });
            byte[] buf = Encoding.UTF8.GetBytes(info);
            ctx.Response.StatusCode = 200;
            await ctx.Response.OutputStream.WriteAsync(buf, ct);
            ctx.Response.Close();
            return;
        }

        if (!path.Equals("/ws", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }

        string clientIp = ExtractRealClientIp(ctx.Request);
        HttpListenerWebSocketContext wsCtx;
        try
        {
            wsCtx = await ctx.AcceptWebSocketAsync(null);
        }
        catch
        {
            ctx.Response.StatusCode = 500;
            ctx.Response.Close();
            return;
        }

        using var ws = wsCtx.WebSocket;
        string sessionId = Guid.NewGuid().ToString("N");
        await HandleClientSessionAsync(sessionId, ws, clientIp, ct);
    }

    private async Task HandleClientSessionAsync(string sessionId, WebSocket ws, string clientIp, CancellationToken ct)
    {
        _store.Reload();
        string challenge = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        bool isLocked = DesktopCaptureAndInput.IsLockScreenActive();

        await SendJsonAsync(ws, new
        {
            type = "hello",
            challenge,
            hostName = _store.Config.HostName,
            isLocked,
            hasMasterPassword = _store.HasMasterPassword
        }, ct);

        bool authenticated = false;
        string? authedDeviceId = null;
        string authedDeviceName = "Android";
        int clientFps = _store.Config.StreamFps;
        int clientQuality = _store.Config.JpegQuality;
        int clientMaxWidth = _store.Config.MaxWidth;
        int selectedMonitor = 0; // Default to Primary Screen (user can switch to Screen 2 or All Screens)

        // 1. Authentication & Pairing Loop (max 5 attempts)
        for (int attempt = 0; attempt < 6 && !authenticated && ws.State == WebSocketState.Open && !ct.IsCancellationRequested; attempt++)
        {
            string? raw = await ReceiveTextAsync(ws, ct);
            if (raw == null) return;

            JsonDocument doc;
            try { doc = JsonDocument.Parse(raw); }
            catch { continue; }

            using (doc)
            {
                var root = doc.RootElement;
                string type = root.TryGetProperty("type", out var tProp) ? tProp.GetString() ?? "" : "";

                if (type == "pair")
                {
                    string deviceId = GetStr(root, "deviceId");
                    string deviceName = GetStr(root, "deviceName");
                    string pairingCode = GetStr(root, "pairingCode");
                    string password = GetStr(root, "password");
                    bool bioVerified = GetBool(root, "biometricVerified");

                    if (!bioVerified)
                    {
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Fingerprint/Biometric verification is required on mobile." }, ct);
                        continue;
                    }
                    if (!_store.HasMasterPassword)
                    {
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Host Master Password is not set yet. Set it on the Windows PC first." }, ct);
                        continue;
                    }
                    if (!_store.ValidatePairingCode(pairingCode))
                    {
                        _store.LogAudit("PAIR_FAILED", deviceId, deviceName, clientIp, "Invalid or expired pairing code.");
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Invalid or expired 6-digit pairing code." }, ct);
                        continue;
                    }
                    if (!_store.VerifyMasterPassword(password))
                    {
                        _store.LogAudit("PAIR_FAILED", deviceId, deviceName, clientIp, "Wrong master password during pairing.");
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Incorrect Master Password." }, ct);
                        continue;
                    }

                    // PC confirmation for new device (if PC is unlocked and handler is attached)
                    if (_store.Config.RequirePcConfirmationForNewDevice && OnApprovalRequired != null && !DesktopCaptureAndInput.IsLockScreenActive())
                    {
                        await SendJsonAsync(ws, new { type = "auth_pending_pc", message = "Waiting for confirmation on Windows PC..." }, ct);
                        bool approved = await OnApprovalRequired(new ConnectionApprovalRequest
                        {
                            DeviceId = deviceId,
                            DeviceName = deviceName,
                            ClientIp = clientIp,
                            Reason = "New mobile device pairing request",
                            IsNewDevice = true
                        });
                        if (!approved)
                        {
                            _store.LogAudit("PAIR_DENIED", deviceId, deviceName, clientIp, "User denied pairing on PC.");
                            await SendJsonAsync(ws, new { type = "auth_error", message = "Pairing request was denied on Windows PC." }, ct);
                            return;
                        }
                    }

                    _store.ConsumePairingCode();
                    var trusted = _store.RegisterTrustedDevice(deviceId, deviceName, clientIp);
                    authenticated = true;
                    authedDeviceId = trusted.DeviceId;
                    authedDeviceName = trusted.DeviceName;

                    var monitors = DesktopCaptureAndInput.GetMonitors();
                    await SendJsonAsync(ws, new
                    {
                        type = "pair_ok",
                        deviceSecret = trusted.DeviceSecretBase64,
                        hostName = _store.Config.HostName,
                        selectedMonitor,
                        monitors = monitors.Select(m => new { index = m.Index, name = m.Name }).ToArray()
                    }, ct);
                }
                else if (type == "auth")
                {
                    string deviceId = GetStr(root, "deviceId");
                    string deviceName = GetStr(root, "deviceName");
                    string proof = GetStr(root, "proof");
                    string password = GetStr(root, "password");
                    bool bioVerified = GetBool(root, "biometricVerified");
                    bool isVpn = GetBool(root, "isVpn");

                    if (!bioVerified)
                    {
                        _store.LogAudit("AUTH_REJECTED", deviceId, deviceName, clientIp, "Biometric verification missing.");
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Fingerprint verification is required." }, ct);
                        continue;
                    }

                    var trusted = _store.GetTrustedDevice(deviceId);
                    bool isTrustedDevice = trusted != null && _store.VerifyDeviceProof(trusted, challenge, proof);

                    var reasons = new List<string>();
                    if (!isTrustedDevice)
                    {
                        reasons.Add("Device Changed / Untrusted Device");
                    }
                    else
                    {
                        if (isVpn)
                            reasons.Add("VPN Detected");
                        if (!string.IsNullOrEmpty(trusted!.LastTrustedIp) &&
                            !string.Equals(trusted.LastTrustedIp, clientIp, StringComparison.OrdinalIgnoreCase))
                            reasons.Add($"IP Changed ({trusted.LastTrustedIp} -> {clientIp})");
                    }

                    // If device is NOT trusted, user must pair (or enter password + pairing code)
                    if (!isTrustedDevice)
                    {
                        _store.LogAudit("UNTRUSTED_DEVICE", deviceId, deviceName, clientIp, "Untrusted device attempted connection.");
                        await SendJsonAsync(ws, new
                        {
                            type = "auth_require_pair",
                            reasons,
                            message = "This device is not trusted. Pair it with Master Password + 6-digit Pairing Code."
                        }, ct);
                        continue;
                    }

                    // 3rd Layer: Conditional Password ONLY if IP changed or VPN detected!
                    if (reasons.Count > 0)
                    {
                        if (string.IsNullOrEmpty(password))
                        {
                            _store.LogAudit("PASSWORD_CHALLENGE", deviceId, trusted!.DeviceName, clientIp, string.Join(", ", reasons));
                            await SendJsonAsync(ws, new
                            {
                                type = "auth_require_password",
                                reasons,
                                message = "Security Check: " + string.Join(" & ", reasons) + ". Enter Master Password."
                            }, ct);
                            continue;
                        }

                        if (!_store.VerifyMasterPassword(password))
                        {
                            _store.LogAudit("PASSWORD_FAILED", deviceId, trusted!.DeviceName, clientIp, "Incorrect master password on risk challenge.");
                            await SendJsonAsync(ws, new
                            {
                                type = "auth_require_password",
                                reasons,
                                message = "Incorrect Master Password. Try again."
                            }, ct);
                            continue;
                        }
                    }

                    // Check lock screen access setting
                    bool pcLockedNow = DesktopCaptureAndInput.IsLockScreenActive();
                    if (pcLockedNow && !_store.Config.AllowLockScreenAccess)
                    {
                        await SendJsonAsync(ws, new { type = "auth_error", message = "Lock-screen remote access is disabled on this PC." }, ct);
                        return;
                    }

                    // Optional PC confirmation when PC is UNLOCKED (if enabled)
                    if (!pcLockedNow && _store.Config.RequirePcConfirmationWhenUnlocked && OnApprovalRequired != null)
                    {
                        await SendJsonAsync(ws, new { type = "auth_pending_pc", message = "Waiting for confirmation on Windows PC..." }, ct);
                        bool approved = await OnApprovalRequired(new ConnectionApprovalRequest
                        {
                            DeviceId = trusted!.DeviceId,
                            DeviceName = trusted.DeviceName,
                            ClientIp = clientIp,
                            Reason = reasons.Count > 0 ? string.Join(", ", reasons) : "Trusted mobile connection",
                            IsNewDevice = false
                        });
                        if (!approved)
                        {
                            _store.LogAudit("CONN_DENIED", trusted.DeviceId, trusted.DeviceName, clientIp, "User denied connection on PC.");
                            await SendJsonAsync(ws, new { type = "auth_error", message = "Connection denied on Windows PC." }, ct);
                            return;
                        }
                    }

                    // Update last trusted IP only when NOT on VPN (so VPN stays protected)
                    _store.UpdateDeviceLastSeen(trusted!.DeviceId, clientIp, updateTrustedIp: !isVpn);
                    _store.LogAudit("AUTH_SUCCESS", trusted.DeviceId, trusted.DeviceName, clientIp,
                        reasons.Count == 0
                            ? $"Direct biometric+device trust login (PC Locked={pcLockedNow})"
                            : $"Verified with password ({string.Join(", ", reasons)}; PC Locked={pcLockedNow})");

                    authenticated = true;
                    authedDeviceId = trusted.DeviceId;
                    authedDeviceName = trusted.DeviceName;

                    var monitors = DesktopCaptureAndInput.GetMonitors();
                    await SendJsonAsync(ws, new
                    {
                        type = "auth_ok",
                        hostName = _store.Config.HostName,
                        isLocked = pcLockedNow,
                        fps = clientFps,
                        quality = clientQuality,
                        selectedMonitor,
                        monitors = monitors.Select(m => new { index = m.Index, name = m.Name }).ToArray()
                    }, ct);
                }
            }
        }

        if (!authenticated) return;

        _activeSessions[sessionId] = ws;
        ActiveClientsChanged?.Invoke(_activeSessions.Count);

        using var sessionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var streamTask = Task.Run(async () =>
        {
            while (!sessionCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var frame = DesktopCaptureAndInput.CaptureFrame(clientMaxWidth, clientQuality, selectedMonitor);
                if (frame != null)
                {
                    var (jpeg, w, h, locked) = frame.Value;
                    // Binary frame format: [1 byte type=0x01][1 byte locked][2 bytes width][2 bytes height][JPEG payload]
                    byte[] packet = new byte[6 + jpeg.Length];
                    packet[0] = 0x01;
                    packet[1] = locked ? (byte)1 : (byte)0;
                    packet[2] = (byte)(w >> 8);
                    packet[3] = (byte)(w & 0xFF);
                    packet[4] = (byte)(h >> 8);
                    packet[5] = (byte)(h & 0xFF);
                    Buffer.BlockCopy(jpeg, 0, packet, 6, jpeg.Length);

                    try
                    {
                        await ws.SendAsync(packet, WebSocketMessageType.Binary, true, sessionCts.Token);
                    }
                    catch
                    {
                        break;
                    }
                }

                int delayMs = Math.Max(20, (1000 / Math.Clamp(clientFps, 3, 30)) - (int)sw.ElapsedMilliseconds);
                try { await Task.Delay(delayMs, sessionCts.Token); } catch { break; }
            }
        }, sessionCts.Token);

        try
        {
            while (!sessionCts.Token.IsCancellationRequested && ws.State == WebSocketState.Open)
            {
                string? msg = await ReceiveTextAsync(ws, sessionCts.Token);
                if (msg == null) break;

                try
                {
                    using var doc = JsonDocument.Parse(msg);
                    var root = doc.RootElement;
                    string type = GetStr(root, "type");

                    switch (type)
                    {
                        case "mouse":
                            DesktopCaptureAndInput.HandleMouseEvent(
                                GetStr(root, "action"),
                                GetDouble(root, "x"),
                                GetDouble(root, "y"),
                                GetInt(root, "delta"),
                                selectedMonitor);
                            break;
                        case "select_monitor":
                            selectedMonitor = GetInt(root, "index", 0);
                            break;
                        case "get_monitors":
                            var mons = DesktopCaptureAndInput.GetMonitors();
                            await SendJsonAsync(ws, new
                            {
                                type = "monitors",
                                selectedMonitor,
                                monitors = mons.Select(m => new { index = m.Index, name = m.Name }).ToArray()
                            }, sessionCts.Token);
                            break;
                        case "text":
                            DesktopCaptureAndInput.HandleText(GetStr(root, "text"));
                            break;
                        case "key":
                            DesktopCaptureAndInput.SendSpecialKey(GetStr(root, "key"));
                            break;
                        case "quality":
                            clientQuality = Math.Clamp(GetInt(root, "quality", clientQuality), 20, 90);
                            clientFps = Math.Clamp(GetInt(root, "fps", clientFps), 4, 25);
                            clientMaxWidth = Math.Clamp(GetInt(root, "maxWidth", clientMaxWidth), 640, 1920);
                            break;
                        case "ping":
                            await SendJsonAsync(ws, new { type = "pong" }, sessionCts.Token);
                            break;
                    }
                }
                catch { }
            }
        }
        finally
        {
            sessionCts.Cancel();
            try { await streamTask; } catch { }
            _activeSessions.TryRemove(sessionId, out _);
            ActiveClientsChanged?.Invoke(_activeSessions.Count);
            _store.LogAudit("DISCONNECTED", authedDeviceId ?? "", authedDeviceName, clientIp, "Session closed.");
        }
    }

    private static async Task SendJsonAsync(WebSocket ws, object payload, CancellationToken ct)
    {
        if (ws.State != WebSocketState.Open) return;
        byte[] bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(payload));
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct);
    }

    private static async Task<string?> ReceiveTextAsync(WebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var ms = new MemoryStream();
        while (ws.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, ct);
            }
            catch
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close) return null;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return Encoding.UTF8.GetString(ms.ToArray());
        }
        return null;
    }

    private static string GetStr(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static bool GetBool(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False) && v.GetBoolean();

    private static double GetDouble(JsonElement e, string prop) =>
        e.TryGetProperty(prop, out var v) && v.TryGetDouble(out double d) ? d : 0;

    private static int GetInt(JsonElement e, string prop, int fallback = 0) =>
        e.TryGetProperty(prop, out var v) && v.TryGetInt32(out int i) ? i : fallback;

    public void Dispose()
    {
        Stop();
    }
}
