# RemoteGuard — 24/7 Secure Windows Remote Control (Windows + Android)

A self-hosted, privacy-first remote desktop solution for controlling your Windows PC from your Android phone — over **Internet (anywhere via Cloudflare Tunnel / Tailscale)** or **Local Wi-Fi**.

## Built Binaries (Ready to Use)

- **Windows Host:** `Release\Windows\RemoteGuard.Host.exe` (self-contained, single-file)
- **Android App:** `Release\Android\RemoteGuard.apk` (install directly on Android 8.0+)

## Key Features

### 1. 3-Layer Privacy & Anti-Hack Protection
1. **Device Trust (Cryptographic Key):** First-time pairing requires a 1-time 6-digit code + Master Password + PC confirmation. The phone receives a unique 256-bit HMAC-SHA256 secret stored in **Android Keystore (`EncryptedSharedPreferences`)**.
2. **Fingerprint / Biometric Lock:** Every single connection requires Android Fingerprint / Biometric authentication on the phone before sending the cryptographic proof.
3. **Conditional Master Password (Not All the Time!):**
   - **Same Trusted Phone + Same IP + No VPN:** Direct instant access with **Fingerprint only** (no password prompt!).
   - **IP Address Changed, VPN Detected, or New/Untrusted Device:** Server automatically challenges for the **Master Password** (PBKDF2-SHA256 verified) before granting access.

### 2. Lock-Screen Access (Even When PC Is Locked)
- Install the built-in **`RemoteGuardService` (LocalSystem Windows Service)** from the **24/7 Power & Lock-Screen Service** tab.
- Runs across Session 0 -> Active Console Session (`winsta0\default` + `OpenInputDesktop` / `SetThreadDesktop`) so your trusted phone can view the Windows Lock Screen, press **Wake Screen** or **Ctrl+Alt+Del**, type your Windows PIN/Password, and unlock/control the PC anytime.

### 3. 24/7 Always-On & Laptop Lid-Closed "Do Nothing" Mode
- One-click policy configures Windows `powercfg`:
  - **Laptop Lid Close = Do Nothing** (AC & Battery)
  - **Auto-Sleep = Never**, **Auto-Hibernate = Never**, **Unattended Sleep = Never**
  - Continuous `SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED)` keep-awake pulse.
- You can still **manually Sleep, Hibernate, or Shut Down** from the Windows Start Menu anytime.

### 4. Internet Access from Anywhere (Zero Port Forwarding)
- Built-in **Cloudflare Tunnel** (`cloudflared.exe`) integration auto-downloads the official Cloudflare client on click and gives you an encrypted `wss://<random>.trycloudflare.com/ws` URL (or use your own permanent Cloudflare Named Tunnel token, Tailscale IP, or LAN IP).

## Quick Setup Guide

1. **On Windows PC:**
   - Run `Release\Windows\RemoteGuard.Host.exe` (runs as Administrator).
   - Open the **Security & Trusted Devices** tab -> set your **Master Password**.
   - Open the **24/7 Power & Lock-Screen Service** tab:
     - Click **Apply 24/7 & Lid-Close Policy**.
     - Click **Install & Start 24/7 Windows Service** (enables Lock Screen control & auto-start on boot).
   - Open the **Connect & Internet** tab:
     - Click **Start Internet Tunnel (Cloudflare)** to get your `wss://...` URL (or use your LAN/Tailscale URL).
     - Click **Generate 6-Digit Code & QR**.
2. **On Android Phone:**
   - Install `Release\Android\RemoteGuard.apk`.
   - Enter the Server URL (`wss://...`), 6-digit Pairing Code, and Master Password -> tap **Pair & Trust This Phone (Fingerprint)**.
   - Approve the confirmation popup on your Windows PC.
   - Done! From now on, just tap **Connect with Fingerprint** — anytime, even when your laptop lid is closed and Windows is locked!
