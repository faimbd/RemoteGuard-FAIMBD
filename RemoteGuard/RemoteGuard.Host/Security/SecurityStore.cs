using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RemoteGuard.Host.Security;

public sealed class TrustedDevice
{
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string DeviceSecretBase64 { get; set; } = string.Empty;
    public string LastTrustedIp { get; set; } = string.Empty;
    public DateTime PairedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime LastSeenUtc { get; set; } = DateTime.UtcNow;
    public bool BiometricEnrolled { get; set; } = true;
}

public sealed class HostConfig
{
    public int Port { get; set; } = 9443;
    public string HostName { get; set; } = Environment.MachineName;
    public string MasterPasswordSalt { get; set; } = string.Empty;
    public string MasterPasswordHash { get; set; } = string.Empty;
    public bool KeepAwake24x7 { get; set; } = true;
    public bool LidCloseDoNothing { get; set; } = true;
    public bool AllowLockScreenAccess { get; set; } = true;
    public bool RequirePcConfirmationForNewDevice { get; set; } = true;
    public bool RequirePcConfirmationWhenUnlocked { get; set; } = false;
    public int StreamFps { get; set; } = 15;
    public int JpegQuality { get; set; } = 55;
    public int MaxWidth { get; set; } = 1280;
    public string CustomTunnelToken { get; set; } = string.Empty;
    public List<TrustedDevice> TrustedDevices { get; set; } = new();
}

public sealed class AuditEntry
{
    public DateTime TimestampUtc { get; set; } = DateTime.UtcNow;
    public string EventType { get; set; } = string.Empty;
    public string DeviceId { get; set; } = string.Empty;
    public string DeviceName { get; set; } = string.Empty;
    public string ClientIp { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

public sealed class SecurityStore
{
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };
    private readonly object _lock = new();

    public static string DataDirectory =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RemoteGuard");

    public static string ConfigPath => Path.Combine(DataDirectory, "config.json");
    public static string PairingPath => Path.Combine(DataDirectory, "pairing.json");
    public static string AuditPath => Path.Combine(DataDirectory, "audit.log");

    public HostConfig Config { get; private set; }
    public string? ActivePairingCode { get; private set; }
    public DateTime PairingCodeExpiresUtc { get; private set; } = DateTime.MinValue;

    public event Action<AuditEntry>? AuditLogged;

    public SecurityStore()
    {
        Directory.CreateDirectory(DataDirectory);
        Config = LoadConfig();
    }

    private HostConfig LoadConfig()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var json = File.ReadAllText(ConfigPath);
                var cfg = JsonSerializer.Deserialize<HostConfig>(json);
                if (cfg != null) return cfg;
            }
        }
        catch { }

        var fresh = new HostConfig();
        SaveConfig(fresh);
        return fresh;
    }

    public void Reload()
    {
        lock (_lock)
        {
            Config = LoadConfig();
        }
    }

    public void SaveConfig(HostConfig? cfg = null)
    {
        lock (_lock)
        {
            if (cfg != null) Config = cfg;
            Directory.CreateDirectory(DataDirectory);
            var tmp = ConfigPath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(Config, JsonOpts));
            File.Move(tmp, ConfigPath, overwrite: true);
        }
    }

    public bool HasMasterPassword => !string.IsNullOrWhiteSpace(Config.MasterPasswordHash);

    public void SetMasterPassword(string password)
    {
        if (string.IsNullOrWhiteSpace(password) || password.Length < 4)
            throw new ArgumentException("Password must be at least 4 characters.");

        byte[] salt = RandomNumberGenerator.GetBytes(32);
        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            120_000,
            HashAlgorithmName.SHA256,
            32);

        lock (_lock)
        {
            Config.MasterPasswordSalt = Convert.ToBase64String(salt);
            Config.MasterPasswordHash = Convert.ToBase64String(hash);
            SaveConfig();
        }
        LogAudit("PASSWORD_UPDATED", "", "", "local", "Master password was updated.");
    }

    public bool VerifyMasterPassword(string password)
    {
        if (string.IsNullOrEmpty(password)) return false;
        lock (_lock)
        {
            if (string.IsNullOrEmpty(Config.MasterPasswordSalt) || string.IsNullOrEmpty(Config.MasterPasswordHash))
                return false;

            byte[] salt = Convert.FromBase64String(Config.MasterPasswordSalt);
            byte[] expected = Convert.FromBase64String(Config.MasterPasswordHash);
            byte[] actual = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                120_000,
                HashAlgorithmName.SHA256,
                32);

            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
    }

    public string GeneratePairingCode(TimeSpan? lifetime = null)
    {
        lock (_lock)
        {
            int num = RandomNumberGenerator.GetInt32(100000, 1000000);
            ActivePairingCode = num.ToString("D6");
            PairingCodeExpiresUtc = DateTime.UtcNow.Add(lifetime ?? TimeSpan.FromMinutes(10));
            try
            {
                var payload = JsonSerializer.Serialize(new { Code = ActivePairingCode, ExpiresUtc = PairingCodeExpiresUtc });
                File.WriteAllText(PairingPath, payload);
            }
            catch { }
            return ActivePairingCode;
        }
    }

    public bool ValidatePairingCode(string code)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(ActivePairingCode) || DateTime.UtcNow > PairingCodeExpiresUtc)
            {
                try
                {
                    if (File.Exists(PairingPath))
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(PairingPath));
                        var c = doc.RootElement.GetProperty("Code").GetString();
                        var exp = doc.RootElement.GetProperty("ExpiresUtc").GetDateTime();
                        if (!string.IsNullOrWhiteSpace(c) && DateTime.UtcNow <= exp)
                        {
                            ActivePairingCode = c;
                            PairingCodeExpiresUtc = exp;
                        }
                    }
                }
                catch { }
            }

            if (string.IsNullOrWhiteSpace(ActivePairingCode) || DateTime.UtcNow > PairingCodeExpiresUtc)
                return false;
            return CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(ActivePairingCode),
                Encoding.UTF8.GetBytes(code.Trim()));
        }
    }

    public void ConsumePairingCode()
    {
        lock (_lock)
        {
            ActivePairingCode = null;
            PairingCodeExpiresUtc = DateTime.MinValue;
            try { if (File.Exists(PairingPath)) File.Delete(PairingPath); } catch { }
        }
    }

    public TrustedDevice? GetTrustedDevice(string deviceId)
    {
        lock (_lock)
        {
            return Config.TrustedDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
        }
    }

    public TrustedDevice RegisterTrustedDevice(string deviceId, string deviceName, string clientIp)
    {
        byte[] secret = RandomNumberGenerator.GetBytes(32);
        string secretB64 = Convert.ToBase64String(secret);

        lock (_lock)
        {
            Config.TrustedDevices.RemoveAll(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));

            var dev = new TrustedDevice
            {
                DeviceId = deviceId,
                DeviceName = string.IsNullOrWhiteSpace(deviceName) ? "Android Device" : deviceName.Trim(),
                DeviceSecretBase64 = secretB64,
                LastTrustedIp = clientIp,
                PairedAtUtc = DateTime.UtcNow,
                LastSeenUtc = DateTime.UtcNow,
                BiometricEnrolled = true
            };
            Config.TrustedDevices.Add(dev);
            SaveConfig();
            LogAudit("DEVICE_PAIRED", dev.DeviceId, dev.DeviceName, clientIp, "New trusted device paired.");
            return dev;
        }
    }

    public bool VerifyDeviceProof(TrustedDevice device, string challenge, string proofBase64)
    {
        try
        {
            byte[] key = Convert.FromBase64String(device.DeviceSecretBase64);
            using var hmac = new HMACSHA256(key);
            byte[] expected = hmac.ComputeHash(Encoding.UTF8.GetBytes(challenge));
            byte[] actual = Convert.FromBase64String(proofBase64);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch
        {
            return false;
        }
    }

    public void UpdateDeviceLastSeen(string deviceId, string clientIp, bool updateTrustedIp)
    {
        lock (_lock)
        {
            var dev = Config.TrustedDevices.FirstOrDefault(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (dev == null) return;
            dev.LastSeenUtc = DateTime.UtcNow;
            if (updateTrustedIp && !string.IsNullOrWhiteSpace(clientIp))
                dev.LastTrustedIp = clientIp;
            SaveConfig();
        }
    }

    public void RevokeDevice(string deviceId)
    {
        lock (_lock)
        {
            var removed = Config.TrustedDevices.RemoveAll(d =>
                string.Equals(d.DeviceId, deviceId, StringComparison.OrdinalIgnoreCase));
            if (removed > 0)
            {
                SaveConfig();
                LogAudit("DEVICE_REVOKED", deviceId, "", "local", "Trusted device revoked.");
            }
        }
    }

    public void LogAudit(string eventType, string deviceId, string deviceName, string clientIp, string details)
    {
        var entry = new AuditEntry
        {
            TimestampUtc = DateTime.UtcNow,
            EventType = eventType,
            DeviceId = deviceId,
            DeviceName = deviceName,
            ClientIp = clientIp,
            Details = details
        };
        try
        {
            lock (_lock)
            {
                Directory.CreateDirectory(DataDirectory);
                string line = $"[{entry.TimestampUtc.ToLocalTime():yyyy-MM-dd HH:mm:ss}] [{eventType}] IP={clientIp} Device={deviceName}({deviceId}) | {details}";
                File.AppendAllText(AuditPath, line + Environment.NewLine);
            }
        }
        catch { }

        AuditLogged?.Invoke(entry);
    }

    public List<string> ReadRecentAuditLines(int maxLines = 100)
    {
        try
        {
            if (!File.Exists(AuditPath)) return new();
            var lines = File.ReadAllLines(AuditPath);
            return lines.TakeLast(maxLines).Reverse().ToList();
        }
        catch
        {
            return new();
        }
    }
}
