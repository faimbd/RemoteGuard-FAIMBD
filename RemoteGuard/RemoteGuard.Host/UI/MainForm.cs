using System.Drawing;
using System.Drawing.Drawing2D;
using System.Reflection;
using System.Text.Json;
using QRCoder;
using RemoteGuard.Host.Network;
using RemoteGuard.Host.Power;
using RemoteGuard.Host.Security;
using RemoteGuard.Host.Service;

namespace RemoteGuard.Host.UI;

public sealed class MainForm : Form
{
    private readonly SecurityStore _store;
    private readonly PowerGuardService _powerGuard;
    private readonly RemoteWebSocketServer _server;
    private readonly TunnelManager _tunnel;
    private readonly NotifyIcon _trayIcon;
    private readonly System.Windows.Forms.Timer _uiRefreshTimer;
    private bool _reallyExit;

    // Status labels
    private Label _lblServerStatus = null!;
    private Label _lblServiceStatus = null!;
    private Label _lblPowerStatus = null!;
    private Label _lblClients = null!;

    // Connect tab
    private TextBox _txtLanUrl = null!;
    private TextBox _txtTunnelUrl = null!;
    private TextBox _txtTunnelToken = null!;
    private Button _btnTunnelToggle = null!;
    private Label _lblPairingCode = null!;
    private PictureBox _picQr = null!;
    private TextBox _txtTunnelLog = null!;

    // Security tab
    private TextBox _txtNewPassword = null!;
    private Label _lblPasswordState = null!;
    private CheckBox _chkAllowLockScreen = null!;
    private CheckBox _chkConfirmNewDevice = null!;
    private CheckBox _chkConfirmUnlocked = null!;
    private ListView _lvDevices = null!;

    // Power tab
    private CheckBox _chkLidDoNothing = null!;
    private CheckBox _chkKeepAwake = null!;
    private Label _lblPowerLog = null!;

    // Audit tab
    private TextBox _txtAudit = null!;

    private readonly Color BgDark = Color.FromArgb(11, 11, 14);
    private readonly Color CardDark = Color.FromArgb(22, 20, 18);
    private readonly Color Accent = Color.FromArgb(245, 197, 66);
    private readonly Color AccentGreen = Color.FromArgb(34, 197, 94);
    private readonly Color AccentRed = Color.FromArgb(239, 68, 68);
    private readonly Color TextPrimary = Color.FromArgb(250, 246, 236);
    private readonly Color TextMuted = Color.FromArgb(186, 174, 144);

    private static Icon LoadBrandIcon()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteGuard.Host.Assets.app.ico");
            if (s != null) return new Icon(s);
        }
        catch { }
        return SystemIcons.Shield;
    }

    private static Image? LoadBrandLogo()
    {
        try
        {
            using var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("RemoteGuard.Host.Assets.logo.png");
            if (s != null) return Image.FromStream(s);
        }
        catch { }
        return null;
    }

    public MainForm(SecurityStore store, PowerGuardService powerGuard, RemoteWebSocketServer server, TunnelManager tunnel)
    {
        _store = store;
        _powerGuard = powerGuard;
        _server = server;
        _tunnel = tunnel;

        var brandIcon = LoadBrandIcon();
        Icon = brandIcon;

        Text = "RemoteGuard Host • FAIM BD — 24/7 Secure Remote Control";
        Size = new Size(920, 680);
        MinimumSize = new Size(860, 620);
        StartPosition = FormStartPosition.CenterScreen;
        BackColor = BgDark;
        ForeColor = TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        _trayIcon = new NotifyIcon
        {
            Text = "RemoteGuard Host • FAIM BD (24/7 Active)",
            Icon = brandIcon,
            Visible = true,
            ContextMenuStrip = BuildTrayMenu()
        };
        _trayIcon.DoubleClick += (_, _) => RestoreFromTray();

        BuildUi();
        WireEvents();
        RefreshAllUi();
        StartLocalHostIfServiceNotRunning();

        _uiRefreshTimer = new System.Windows.Forms.Timer { Interval = 3000 };
        _uiRefreshTimer.Tick += (_, _) =>
        {
            if (!Visible) return;
            RefreshSecurityDevicesOnly();
            RefreshAuditLog();
            RefreshStatusBadges();
        };
        _uiRefreshTimer.Start();
    }

    private ContextMenuStrip BuildTrayMenu()
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("Open Dashboard", null, (_, _) => RestoreFromTray());
        menu.Items.Add("Generate Pairing Code", null, (_, _) => { RestoreFromTray(); GenerateNewPairingCode(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit RemoteGuard", null, (_, _) => { _reallyExit = true; Close(); });
        return menu;
    }

    private void RestoreFromTray()
    {
        Show();
        WindowState = FormWindowState.Normal;
        Activate();
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!_reallyExit && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip(2000, "RemoteGuard", "Running in background (24/7 Guard Active).", ToolTipIcon.Info);
            return;
        }

        _trayIcon.Visible = false;
        _trayIcon.Dispose();
        _tunnel.Dispose();
        _server.Dispose();
        _powerGuard.Dispose();
        base.OnFormClosing(e);
    }

    private void BuildUi()
    {
        // Top Header Banner (Golden & Black Mix Gradient)
        var header = new Panel { Dock = DockStyle.Top, Height = 92, BackColor = CardDark, Padding = new Padding(18, 12, 18, 10) };
        header.Paint += (s, e) =>
        {
            using var brush = new LinearGradientBrush(header.ClientRectangle,
                Color.FromArgb(12, 12, 15),
                Color.FromArgb(62, 46, 14),
                LinearGradientMode.Horizontal);
            e.Graphics.FillRectangle(brush, header.ClientRectangle);
            using var pen = new Pen(Color.FromArgb(212, 175, 55), 2f);
            e.Graphics.DrawLine(pen, 0, header.Height - 1, header.Width, header.Height - 1);
        };

        var picLogo = new PictureBox
        {
            Location = new Point(16, 14),
            Size = new Size(62, 62),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.Transparent,
            Image = LoadBrandLogo()
        };

        var lblTitle = new Label
        {
            Text = "RemoteGuard • FAIM BD",
            Font = new Font("Segoe UI Semibold", 16f),
            ForeColor = Accent,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(90, 14)
        };
        var lblSub = new Label
        {
            Text = "3-Layer Privacy (Device Trust + Fingerprint + IP/VPN Password) • 24/7 Lid-Closed Guard",
            ForeColor = TextMuted,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(92, 46)
        };

        _lblServerStatus = MakeBadge(550, 16, "Server: Starting...", Accent);
        _lblServiceStatus = MakeBadge(715, 16, "Service: Checking...", TextMuted);
        _lblPowerStatus = MakeBadge(550, 48, "24/7 Guard: Active", AccentGreen);
        _lblClients = MakeBadge(715, 48, "Connected: 0", TextPrimary);
        _lblServerStatus.BackColor = Color.Transparent;
        _lblServiceStatus.BackColor = Color.Transparent;
        _lblPowerStatus.BackColor = Color.Transparent;
        _lblClients.BackColor = Color.Transparent;

        header.Controls.AddRange(new Control[] { picLogo, lblTitle, lblSub, _lblServerStatus, _lblServiceStatus, _lblPowerStatus, _lblClients });
        Controls.Add(header);

        // Tabs
        var tabs = new TabControl
        {
            Dock = DockStyle.Fill,
            Padding = new Point(14, 6),
            Font = new Font("Segoe UI Semibold", 9.5f)
        };

        tabs.TabPages.Add(BuildConnectTab());
        tabs.TabPages.Add(BuildSecurityTab());
        tabs.TabPages.Add(BuildPowerTab());
        tabs.TabPages.Add(BuildAuditTab());

        var mainWrap = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12) };
        mainWrap.Controls.Add(tabs);
        Controls.Add(mainWrap);
        mainWrap.BringToFront();
    }

    private Label MakeBadge(int x, int y, string text, Color color) => new()
    {
        Text = text,
        ForeColor = color,
        Font = new Font("Segoe UI Semibold", 9f),
        AutoSize = true,
        Location = new Point(x, y)
    };

    private Button MakeButton(string text, Color bg, int w = 170, int h = 34)
    {
        var btn = new Button
        {
            Text = text,
            Width = w,
            Height = h,
            FlatStyle = FlatStyle.Flat,
            BackColor = bg,
            ForeColor = Color.White,
            Font = new Font("Segoe UI Semibold", 9f),
            Cursor = Cursors.Hand
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private TabPage BuildConnectTab()
    {
        var page = new TabPage("Connect & Internet") { BackColor = BgDark, ForeColor = TextPrimary, Padding = new Padding(14) };

        // Left panel: Connection URLs & Cloudflare Tunnel
        var left = new Panel { Dock = DockStyle.Left, Width = 520, BackColor = CardDark, Padding = new Padding(14) };

        var lbl1 = new Label { Text = "Local / Tailscale WebSocket URL", ForeColor = TextMuted, AutoSize = true, Location = new Point(14, 14) };
        _txtLanUrl = new TextBox { Location = new Point(14, 36), Width = 400, ReadOnly = true, BackColor = BgDark, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle };
        var btnCopyLan = MakeButton("Copy", Color.FromArgb(51, 65, 85), 76, 26);
        btnCopyLan.Location = new Point(422, 35);
        btnCopyLan.Click += (_, _) => CopyText(_txtLanUrl.Text);

        var lbl2 = new Label { Text = "Internet Tunnel URL (Anywhere via Mobile Data / Wi-Fi)", ForeColor = Accent, AutoSize = true, Location = new Point(14, 76) };
        _txtTunnelUrl = new TextBox { Location = new Point(14, 98), Width = 330, ReadOnly = true, BackColor = BgDark, ForeColor = AccentGreen, BorderStyle = BorderStyle.FixedSingle, Text = "(Click 'Start Internet Tunnel' below)" };
        var btnCopyTun = MakeButton("Copy", Color.FromArgb(51, 65, 85), 68, 26);
        btnCopyTun.Location = new Point(350, 97);
        btnCopyTun.Click += (_, _) => CopyText(_txtTunnelUrl.Text);

        var btnTunnelQr = MakeButton("URL QR", Color.FromArgb(180, 130, 20), 76, 26);
        btnTunnelQr.Location = new Point(422, 97);
        btnTunnelQr.Click += (_, _) =>
        {
            string url = _txtTunnelUrl.Text.Trim();
            if (!url.StartsWith("ws", StringComparison.OrdinalIgnoreCase))
            {
                MessageBox.Show("Start Internet Tunnel first to generate a Tunnel URL QR code.", "Tunnel URL QR", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            RenderQrCode(url, _store.ActivePairingCode ?? "");
        };

        var lblTok = new Label { Text = "Optional: Cloudflare Named Tunnel Token (blank = free Quick Tunnel)", ForeColor = TextMuted, AutoSize = true, Location = new Point(14, 136) };
        _txtTunnelToken = new TextBox { Location = new Point(14, 158), Width = 484, BackColor = BgDark, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle, UseSystemPasswordChar = true };

        _btnTunnelToggle = MakeButton("Start Internet Tunnel (Cloudflare)", Color.FromArgb(2, 132, 199), 260, 36);
        _btnTunnelToggle.Location = new Point(14, 196);
        _btnTunnelToggle.Click += async (_, _) => await ToggleTunnelAsync();

        var btnFirewall = MakeButton("Allow Firewall Port", Color.FromArgb(51, 65, 85), 160, 36);
        btnFirewall.Location = new Point(284, 196);
        btnFirewall.Click += (_, _) =>
        {
            TunnelManager.EnsureFirewallRule(_store.Config.Port);
            MessageBox.Show($"Firewall rule added for TCP port {_store.Config.Port}.", "Firewall", MessageBoxButtons.OK, MessageBoxIcon.Information);
        };

        var lblLog = new Label { Text = "Tunnel Status Log", ForeColor = TextMuted, AutoSize = true, Location = new Point(14, 246) };
        _txtTunnelLog = new TextBox
        {
            Location = new Point(14, 268),
            Width = 484,
            Height = 200,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            BackColor = BgDark,
            ForeColor = TextMuted,
            BorderStyle = BorderStyle.FixedSingle,
            Font = new Font("Consolas", 8.5f)
        };

        left.Controls.AddRange(new Control[] { lbl1, _txtLanUrl, btnCopyLan, lbl2, _txtTunnelUrl, btnCopyTun, btnTunnelQr, lblTok, _txtTunnelToken, _btnTunnelToggle, btnFirewall, lblLog, _txtTunnelLog });

        // Right panel: QR Code & Pairing
        var right = new Panel { Dock = DockStyle.Fill, BackColor = CardDark, Padding = new Padding(14) };
        var lblPairTitle = new Label { Text = "Pair Your Android Phone", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = TextPrimary, AutoSize = true, Location = new Point(18, 14) };
        var lblPairSub = new Label { Text = "1. Set Master Password in Security tab\n2. Click Generate Code below\n3. Enter URL + Code in RemoteGuard APK", ForeColor = TextMuted, AutoSize = true, Location = new Point(18, 42) };

        _lblPairingCode = new Label
        {
            Text = "------",
            Font = new Font("Consolas", 26f, FontStyle.Bold),
            ForeColor = AccentGreen,
            AutoSize = true,
            Location = new Point(78, 102)
        };

        var btnGen = MakeButton("Generate 6-Digit Code & QR", Color.FromArgb(22, 163, 74), 260, 36);
        btnGen.Location = new Point(22, 156);
        btnGen.Click += (_, _) => GenerateNewPairingCode();

        _picQr = new PictureBox
        {
            Location = new Point(52, 206),
            Size = new Size(210, 210),
            SizeMode = PictureBoxSizeMode.Zoom,
            BackColor = Color.White
        };

        right.Controls.AddRange(new Control[] { lblPairTitle, lblPairSub, _lblPairingCode, btnGen, _picQr });

        var spacer = new Panel { Dock = DockStyle.Left, Width = 12, BackColor = BgDark };
        page.Controls.Add(right);
        page.Controls.Add(spacer);
        page.Controls.Add(left);
        return page;
    }

    private TabPage BuildSecurityTab()
    {
        var page = new TabPage("Security & Trusted Devices") { BackColor = BgDark, ForeColor = TextPrimary, Padding = new Padding(14) };

        var topCard = new Panel { Dock = DockStyle.Top, Height = 190, BackColor = CardDark, Padding = new Padding(14) };
        var lblInfo = new Label
        {
            Text = "3-Layer Privacy Rule: Trusted Mobile + Fingerprint = Direct Login (even when PC is Locked).\nMaster Password is ONLY asked when: (1) IP Changes, (2) VPN is Detected, or (3) New/Changed Device.",
            ForeColor = Accent,
            AutoSize = true,
            Location = new Point(14, 12)
        };

        _lblPasswordState = new Label { Text = "Master Password: Not Set", ForeColor = AccentRed, AutoSize = true, Location = new Point(14, 54) };
        _txtNewPassword = new TextBox { Location = new Point(14, 78), Width = 280, UseSystemPasswordChar = true, BackColor = BgDark, ForeColor = TextPrimary, BorderStyle = BorderStyle.FixedSingle };
        var btnSavePwd = MakeButton("Set / Update Master Password", Color.FromArgb(2, 132, 199), 220, 28);
        btnSavePwd.Location = new Point(304, 77);
        btnSavePwd.Click += (_, _) =>
        {
            try
            {
                _store.SetMasterPassword(_txtNewPassword.Text);
                _txtNewPassword.Clear();
                RefreshSecurityUi();
                MessageBox.Show("Master Password saved securely (PBKDF2-SHA256).", "Security", MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "Password Error", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };

        _chkAllowLockScreen = new CheckBox { Text = "Allow Trusted Device to access PC even when Windows is Locked", AutoSize = true, Location = new Point(14, 118), ForeColor = TextPrimary };
        _chkConfirmNewDevice = new CheckBox { Text = "Require PC Confirmation popup when pairing a NEW mobile device", AutoSize = true, Location = new Point(14, 142), ForeColor = TextPrimary };
        _chkConfirmUnlocked = new CheckBox { Text = "Also ask PC Confirmation popup when PC is unlocked", AutoSize = true, Location = new Point(460, 142), ForeColor = TextPrimary };

        void SaveSecFlags(object? s, EventArgs e)
        {
            _store.Config.AllowLockScreenAccess = _chkAllowLockScreen.Checked;
            _store.Config.RequirePcConfirmationForNewDevice = _chkConfirmNewDevice.Checked;
            _store.Config.RequirePcConfirmationWhenUnlocked = _chkConfirmUnlocked.Checked;
            _store.SaveConfig();
        }
        _chkAllowLockScreen.CheckedChanged += SaveSecFlags;
        _chkConfirmNewDevice.CheckedChanged += SaveSecFlags;
        _chkConfirmUnlocked.CheckedChanged += SaveSecFlags;

        topCard.Controls.AddRange(new Control[] { lblInfo, _lblPasswordState, _txtNewPassword, btnSavePwd, _chkAllowLockScreen, _chkConfirmNewDevice, _chkConfirmUnlocked });

        var devCard = new Panel { Dock = DockStyle.Fill, BackColor = CardDark, Padding = new Padding(14) };
        var lblDev = new Label { Text = "Trusted Mobile Devices", Font = new Font("Segoe UI Semibold", 10.5f), AutoSize = true, Location = new Point(14, 10) };
        var btnRefreshDev = MakeButton("Refresh List", Color.FromArgb(51, 65, 85), 120, 30);
        btnRefreshDev.Location = new Point(520, 6);
        btnRefreshDev.Click += (_, _) => RefreshSecurityUi();

        var btnRevoke = MakeButton("Revoke Selected Device", AccentRed, 190, 30);
        btnRevoke.Location = new Point(650, 6);
        btnRevoke.Click += (_, _) =>
        {
            if (_lvDevices.SelectedItems.Count == 0) return;
            string devId = _lvDevices.SelectedItems[0].SubItems[1].Text;
            _store.RevokeDevice(devId);
            RefreshSecurityUi();
        };

        _lvDevices = new ListView
        {
            Location = new Point(14, 44),
            Width = 826,
            Height = 230,
            View = View.Details,
            FullRowSelect = true,
            BackColor = BgDark,
            ForeColor = TextPrimary,
            BorderStyle = BorderStyle.FixedSingle
        };
        _lvDevices.Columns.Add("Device Name", 180);
        _lvDevices.Columns.Add("Device ID", 240);
        _lvDevices.Columns.Add("Last Trusted IP", 160);
        _lvDevices.Columns.Add("Last Seen (Local)", 220);

        devCard.Controls.AddRange(new Control[] { lblDev, btnRefreshDev, btnRevoke, _lvDevices });

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = BgDark };
        page.Controls.Add(devCard);
        page.Controls.Add(spacer);
        page.Controls.Add(topCard);
        return page;
    }

    private TabPage BuildPowerTab()
    {
        var page = new TabPage("24/7 Power & Lock-Screen Service") { BackColor = BgDark, ForeColor = TextPrimary, Padding = new Padding(14) };

        var card1 = new Panel { Dock = DockStyle.Top, Height = 230, BackColor = CardDark, Padding = new Padding(16) };
        var lblPTitle = new Label { Text = "24/7 Always-On & Laptop Lid Guard", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Accent, AutoSize = true, Location = new Point(16, 14) };
        var lblPDesc = new Label
        {
            Text = "Keeps your Windows laptop running 24/7 even when the lid is closed (Do Nothing mode) and blocks\nautomatic sleep/hibernation. You can still manually Sleep, Hibernate, or Shut Down anytime.",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(16, 42)
        };

        _chkLidDoNothing = new CheckBox { Text = "Laptop Lid Close = Do Nothing (AC & Battery)", AutoSize = true, Location = new Point(16, 92), ForeColor = TextPrimary };
        _chkKeepAwake = new CheckBox { Text = "Never Auto-Sleep / Never Auto-Hibernate (24/7 Always On)", AutoSize = true, Location = new Point(16, 120), ForeColor = TextPrimary };

        var btnApplyPower = MakeButton("Apply 24/7 & Lid-Close Policy", Color.FromArgb(22, 163, 74), 240, 36);
        btnApplyPower.Location = new Point(16, 156);
        btnApplyPower.Click += (_, _) =>
        {
            _store.Config.LidCloseDoNothing = _chkLidDoNothing.Checked;
            _store.Config.KeepAwake24x7 = _chkKeepAwake.Checked;
            _store.SaveConfig();

            var (ok, msg) = PowerGuardService.ApplyAlwaysOnAndLidDoNothing(_chkLidDoNothing.Checked, _chkKeepAwake.Checked);
            _powerGuard.StartKeepAwake(_chkKeepAwake.Checked);
            _lblPowerLog.Text = msg;
            _lblPowerLog.ForeColor = ok ? AccentGreen : AccentRed;
            RefreshStatusBadges();
        };

        var btnRestorePower = MakeButton("Restore Default Windows Power", Color.FromArgb(71, 85, 105), 230, 36);
        btnRestorePower.Location = new Point(268, 156);
        btnRestorePower.Click += (_, _) =>
        {
            _powerGuard.StartKeepAwake(false);
            var (ok, msg) = PowerGuardService.RestoreDefaultPowerPolicy();
            _lblPowerLog.Text = msg;
            _lblPowerLog.ForeColor = ok ? Accent : AccentRed;
            RefreshStatusBadges();
        };

        _lblPowerLog = new Label { Text = "", AutoSize = true, Location = new Point(16, 200), ForeColor = AccentGreen };
        card1.Controls.AddRange(new Control[] { lblPTitle, lblPDesc, _chkLidDoNothing, _chkKeepAwake, btnApplyPower, btnRestorePower, _lblPowerLog });

        var card2 = new Panel { Dock = DockStyle.Fill, BackColor = CardDark, Padding = new Padding(16) };
        var lblSTitle = new Label { Text = "Windows Lock-Screen & 24/7 Background Service", Font = new Font("Segoe UI Semibold", 11.5f), ForeColor = Accent, AutoSize = true, Location = new Point(16, 14) };
        var lblSDesc = new Label
        {
            Text = "Installing the LocalSystem Windows Service allows RemoteGuard to start automatically at boot,\ncapture and control the Windows Lock Screen (Win+L / Ctrl+Alt+Del), and survive sign-outs.",
            ForeColor = TextMuted,
            AutoSize = true,
            Location = new Point(16, 44)
        };

        var btnInstallSvc = MakeButton("Install & Start 24/7 Windows Service", Color.FromArgb(2, 132, 199), 270, 38);
        btnInstallSvc.Location = new Point(16, 100);
        btnInstallSvc.Click += (_, _) =>
        {
            _server.Stop();
            var (ok, msg) = RemoteGuardWindowsService.InstallAndStartService();
            MessageBox.Show(msg, "Windows Service", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Error);
            RefreshStatusBadges();
        };

        var btnUninstallSvc = MakeButton("Stop & Uninstall Service", AccentRed, 200, 38);
        btnUninstallSvc.Location = new Point(298, 100);
        btnUninstallSvc.Click += (_, _) =>
        {
            var (ok, msg) = RemoteGuardWindowsService.UninstallService();
            MessageBox.Show(msg, "Windows Service", MessageBoxButtons.OK, ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
            StartLocalHostIfServiceNotRunning();
            RefreshStatusBadges();
        };

        card2.Controls.AddRange(new Control[] { lblSTitle, lblSDesc, btnInstallSvc, btnUninstallSvc });

        var spacer = new Panel { Dock = DockStyle.Top, Height = 12, BackColor = BgDark };
        page.Controls.Add(card2);
        page.Controls.Add(spacer);
        page.Controls.Add(card1);
        return page;
    }

    private TabPage BuildAuditTab()
    {
        var page = new TabPage("Security Audit Log") { BackColor = BgDark, ForeColor = TextPrimary, Padding = new Padding(14) };
        var btnRefresh = MakeButton("Refresh Log", Color.FromArgb(51, 65, 85), 140, 30);
        btnRefresh.Location = new Point(14, 10);
        btnRefresh.Click += (_, _) => RefreshAuditLog();

        _txtAudit = new TextBox
        {
            Location = new Point(14, 48),
            Width = 840,
            Height = 440,
            Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            BackColor = CardDark,
            ForeColor = TextPrimary,
            Font = new Font("Consolas", 9f),
            BorderStyle = BorderStyle.FixedSingle
        };

        page.Controls.AddRange(new Control[] { btnRefresh, _txtAudit });
        return page;
    }

    private void WireEvents()
    {
        _server.StatusChanged += s => BeginInvoke(() => _lblServerStatus.Text = $"Server: {s}");
        _server.ActiveClientsChanged += count => BeginInvoke(() => _lblClients.Text = $"Connected: {count}");
        _server.OnApprovalRequired = req =>
        {
            var tcs = new TaskCompletionSource<bool>();
            BeginInvoke(() =>
            {
                var res = MessageBox.Show(
                    $"Incoming Mobile Connection Request!\n\nDevice: {req.DeviceName}\nDevice ID: {req.DeviceId}\nClient IP: {req.ClientIp}\nReason: {req.Reason}\n\nDo you want to ALLOW this connection?",
                    "RemoteGuard — Connection Confirmation",
                    MessageBoxButtons.YesNo,
                    MessageBoxIcon.Warning,
                    MessageBoxDefaultButton.Button2);
                tcs.TrySetResult(res == DialogResult.Yes);
            });
            return tcs.Task;
        };

        _tunnel.TunnelUrlReady += url => BeginInvoke(() =>
        {
            string wssUrl = url.Replace("https://", "wss://") + "/ws";
            _txtTunnelUrl.Text = wssUrl;
            // Always auto-render QR code for the new Internet Tunnel URL (includes pairing code if active)
            RenderQrCode(wssUrl, _store.ActivePairingCode ?? "");
        });
        _tunnel.TunnelLog += line => BeginInvoke(() =>
        {
            _txtTunnelLog.AppendText(line + Environment.NewLine);
        });
        _store.AuditLogged += _ => BeginInvoke(() =>
        {
            RefreshAuditLog();
            RefreshSecurityDevicesOnly();
        });
    }

    private void RefreshSecurityDevicesOnly()
    {
        _store.Reload();
        var devices = _store.Config.TrustedDevices;
        if (_lvDevices.Items.Count == devices.Count)
        {
            bool same = true;
            for (int i = 0; i < devices.Count; i++)
            {
                if (_lvDevices.Items[i].SubItems[1].Text != devices[i].DeviceId ||
                    _lvDevices.Items[i].SubItems[2].Text != devices[i].LastTrustedIp)
                {
                    same = false;
                    break;
                }
            }
            if (same) return;
        }
        _lvDevices.Items.Clear();
        foreach (var d in devices)
        {
            var item = new ListViewItem(d.DeviceName);
            item.SubItems.Add(d.DeviceId);
            item.SubItems.Add(d.LastTrustedIp);
            item.SubItems.Add(d.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            _lvDevices.Items.Add(item);
        }
    }

    private void StartLocalHostIfServiceNotRunning()
    {
        string svcStatus = RemoteGuardWindowsService.GetServiceStatus();
        if (svcStatus.StartsWith("Running", StringComparison.OrdinalIgnoreCase))
        {
            _lblServerStatus.Text = "Server: Hosted by 24/7 Service";
            _lblServerStatus.ForeColor = AccentGreen;
        }
        else
        {
            try
            {
                _server.Start();
            }
            catch (Exception ex)
            {
                _lblServerStatus.Text = $"Server Error: {ex.Message}";
                _lblServerStatus.ForeColor = AccentRed;
            }
        }

        if (_store.Config.KeepAwake24x7)
            _powerGuard.StartKeepAwake(true);
    }

    private void RefreshAllUi()
    {
        var (lanIp, tsIp) = TunnelManager.GetLocalAddresses();
        int port = _store.Config.Port;
        _txtLanUrl.Text = tsIp != null
            ? $"ws://{tsIp}:{port}/ws  (LAN: ws://{lanIp}:{port}/ws)"
            : $"ws://{lanIp}:{port}/ws";
        _txtTunnelToken.Text = _store.Config.CustomTunnelToken;

        _chkLidDoNothing.Checked = _store.Config.LidCloseDoNothing;
        _chkKeepAwake.Checked = _store.Config.KeepAwake24x7;

        RefreshSecurityUi();
        RefreshStatusBadges();
        RefreshAuditLog();
    }

    private void RefreshSecurityUi()
    {
        _store.Reload();
        _lblPasswordState.Text = _store.HasMasterPassword
            ? "Master Password: SET (PBKDF2-SHA256 Protected)"
            : "Master Password: NOT SET (Required before pairing!)";
        _lblPasswordState.ForeColor = _store.HasMasterPassword ? AccentGreen : AccentRed;

        _chkAllowLockScreen.Checked = _store.Config.AllowLockScreenAccess;
        _chkConfirmNewDevice.Checked = _store.Config.RequirePcConfirmationForNewDevice;
        _chkConfirmUnlocked.Checked = _store.Config.RequirePcConfirmationWhenUnlocked;

        _lvDevices.Items.Clear();
        foreach (var d in _store.Config.TrustedDevices)
        {
            var item = new ListViewItem(d.DeviceName);
            item.SubItems.Add(d.DeviceId);
            item.SubItems.Add(d.LastTrustedIp);
            item.SubItems.Add(d.LastSeenUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"));
            _lvDevices.Items.Add(item);
        }
    }

    private void RefreshStatusBadges()
    {
        string svc = RemoteGuardWindowsService.GetServiceStatus();
        _lblServiceStatus.Text = $"Service: {svc}";
        _lblServiceStatus.ForeColor = svc.StartsWith("Running") ? AccentGreen : TextMuted;

        _lblPowerStatus.Text = _powerGuard.IsKeepAwakeActive ? "24/7 Guard: Active" : "24/7 Guard: Off";
        _lblPowerStatus.ForeColor = _powerGuard.IsKeepAwakeActive ? AccentGreen : TextMuted;
    }

    private void RefreshAuditLog()
    {
        var lines = _store.ReadRecentAuditLines(120);
        _txtAudit.Text = string.Join(Environment.NewLine, lines);
    }

    private void GenerateNewPairingCode()
    {
        if (!_store.HasMasterPassword)
        {
            MessageBox.Show("Please set a Master Password in the 'Security & Trusted Devices' tab first.", "Master Password Required", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }
        string code = _store.GeneratePairingCode(TimeSpan.FromMinutes(10));
        _lblPairingCode.Text = code;

        string targetUrl = _tunnel.PublicTunnelUrl != null
            ? _tunnel.PublicTunnelUrl.Replace("https://", "wss://") + "/ws"
            : _txtLanUrl.Text.Split(' ')[0];

        RenderQrCode(targetUrl, code);
    }

    private void RenderQrCode(string url, string code)
    {
        try
        {
            string payload = JsonSerializer.Serialize(new { url, code, host = _store.Config.HostName });
            using var qrGen = new QRCodeGenerator();
            using var qrData = qrGen.CreateQrCode(payload, QRCodeGenerator.ECCLevel.M);
            using var qrCode = new QRCode(qrData);
            _picQr.Image?.Dispose();
            _picQr.Image = qrCode.GetGraphic(6);
        }
        catch { }
    }

    private async Task ToggleTunnelAsync()
    {
        if (_tunnel.IsTunnelRunning)
        {
            _tunnel.StopTunnel();
            _btnTunnelToggle.Text = "Start Internet Tunnel (Cloudflare)";
            _btnTunnelToggle.BackColor = Color.FromArgb(2, 132, 199);
            _txtTunnelUrl.Text = "(Tunnel stopped)";
            return;
        }

        _store.Config.CustomTunnelToken = _txtTunnelToken.Text.Trim();
        _store.SaveConfig();

        _btnTunnelToggle.Enabled = false;
        _btnTunnelToggle.Text = "Starting Cloudflare Tunnel...";
        _txtTunnelLog.Clear();

        try
        {
            await _tunnel.StartQuickTunnelAsync(_store.Config.Port, _store.Config.CustomTunnelToken);
            _btnTunnelToggle.Text = "Stop Internet Tunnel";
            _btnTunnelToggle.BackColor = AccentRed;
        }
        catch (Exception ex)
        {
            MessageBox.Show("Tunnel failed: " + ex.Message, "Cloudflare Tunnel", MessageBoxButtons.OK, MessageBoxIcon.Error);
            _btnTunnelToggle.Text = "Start Internet Tunnel (Cloudflare)";
        }
        finally
        {
            _btnTunnelToggle.Enabled = true;
        }
    }

    private static void CopyText(string text)
    {
        if (!string.IsNullOrWhiteSpace(text))
            Clipboard.SetText(text.Split(' ')[0]);
    }
}
