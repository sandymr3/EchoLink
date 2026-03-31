<div align="center">

<h1>EchoLink - Sync With Devices</h1>

Secure, SSH-based device-to-device connectivity and sharing across Windows, Linux, and Android—powered by a self-hosted Headscale tailnet.

<a href='https://github.com/uganthan2005/EchoLink'><img src='https://img.shields.io/badge/Project-Page-green'></a>
<a href='https://github.com/uganthan2005/EchoLink/issues'><img src='https://img.shields.io/badge/Contributions-Welcome-blue'></a>
<a href='https://dotnet.microsoft.com/'><img src='https://img.shields.io/badge/.NET-10-purple'></a>

</div>

---

## What is EchoLink?

> **TL;DR:** EchoLink connects your devices into a private mesh network and uses SSH/SFTP to enable seamless clipboard sync, file transfers, remote actions, and system monitoring between any two online devices—keeping your data entirely off third-party clouds.

### The Problem

You have a Windows desktop, a Linux laptop, and an Android phone. You want to:
- Copy text on your phone and paste it on your desktop
- Send a file to your laptop without uploading it to some cloud first
- Lock your desktop from your phone when you're away
- Use your phone as a microphone for your PC
- Control your PC's mouse from your phone

Existing solutions either require cloud services, work only on specific platforms, or need complex network configuration.

### The Solution

EchoLink creates a **private mesh network** between your devices using Tailscale (self-hosted via Headscale). Once connected, devices communicate directly over SSH and a custom TCP protocol—no cloud relays, no third parties, no port forwarding.

```
┌──────────────┐                    ┌──────────────┐
│   Windows    │◄──────SSH─────────►│    Linux     │
│   Desktop    │◄────Port 55555────►│   Laptop     │
└──────┬───────┘                    └──────┬───────┘
       │                                   │
       │         ┌──────────────┐          │
       └────────►│   Android    │◄─────────┘
                 │    Phone     │
                 └──────────────┘

       All traffic flows over Tailscale mesh (100.x.y.z)
       No internet required after initial auth
```

---

## ✨ Features

### Core
- 🔐 **Private Mesh Network** - Self-hosted Tailscale via Headscale, zero cloud dependency
- 📱 **Cross-Platform** - Windows, Linux, Android with consistent UX
- 🔑 **NodeId-Based Identity** - Devices maintain identity across IP changes
- 🛡️ **SSH Security** - ed25519/RSA key exchange, no passwords
- 🔄 **Smart Device Discovery** - Automatic IP tracking, no duplicate devices

### File & Clipboard
- 📋 **Clipboard Sync** - Auto-broadcast clipboard changes or manual push (SnapShare)
- 📁 **File Transfer** - SFTP-based transfer with progress tracking, remote browsing
- 📤 **Multi-Device Support** - Send to multiple devices simultaneously

### Remote Control & Monitoring
- 🖱️ **Mouse Control** - Trackpad-style remote mouse movement with left/right/middle click buttons
- ⚡ **System Actions** - Lock, restart, shutdown remote devices
- 📊 **System Monitor** - Real-time CPU, RAM, disk, battery, temperature
- 🎤 **Audio Streaming** - Two-way audio (use phone as mic, or stream PC audio)

### Automation
- ⚙️ **Macros** - Custom command buttons synced across devices
- 🔔 **File Watchers** - Auto-reload macros on changes

---

## 🛠️ Tech Stack

| Component | Technology |
|-----------|------------|
| **UI Framework** | Avalonia UI 11 (cross-platform XAML) |
| **Language** | C# (.NET 10), Go (Android native bridge) |
| **Mesh Network** | Tailscale + Headscale (self-hosted) |
| **File Transfer** | SSH.NET (SFTP over SSH) |
| **Audio Codec** | Opus (48kHz, low-latency) |
| **Audio I/O** | NAudio (Windows), Native bridges (Linux/Android) |
| **Protocol** | Custom TCP on port 55555 + SOCKS5 proxy |

---

## 📑 Project Status

### Completed ✅

- [x] Headscale control server integration (`control.echo-link.app`)
- [x] Google OIDC authentication with middleware PIN exchange
- [x] Tailscale daemon with userspace networking + SOCKS5 proxy (port 1055)
- [x] Cross-platform UI with dashboard, device discovery, QR/PIN pairing
- [x] SSH key pair generation and bidirectional exchange (port 44444)
- [x] SFTP file transfer with remote browsing, progress tracking, Android path handling
- [x] Clipboard sync with MirrorClip (auto), SnapShare (manual), GhostPaste (remote apply)
- [x] Remote mouse control with trackpad + click buttons (left/right/middle)
- [x] System commands (lock/restart/shutdown)
- [x] System monitoring (CPU/RAM/disk/battery/temp) with 10s polling
- [x] Two-way audio streaming with Opus encoding, VB-Audio Cable routing
- [x] Macro system with mesh sync and file watcher auto-reload
- [x] Android Go bridge with `tsnet`, SSH server on port 2222, native audio capture
- [x] Unified TCP protocol (port 55555) for all non-SSH features
- [x] Guest device support with time-limited PINs
- [x] NodeId-based device identity (no duplicates on IP change)
- [x] Automatic device refresh with selection preservation

### In Progress / Needs Polish 🚧

- [ ] Automated VB-Audio Cable driver installation (Windows)
- [ ] Linux audio capture bridge (PulseAudio/PipeWire integration)
- [ ] macOS full support (audio, remote control implementations)
- [ ] Hotkey registration system (UI exists, backend incomplete)
- [ ] Mesh topology visualization
- [ ] Quickstart documentation

### Planned 📋

- [ ] iOS client
- [ ] LAN-only fallback mode (offline operation)
- [ ] End-to-end encryption beyond SSH
- [ ] Network graph visualization

---

## 🏗️ Architecture

### How It Works

**1. Authentication**
```
User Login → Google OIDC → JWT → Middleware → Tailscale Auth Key → Daemon Starts
```

**2. Device Pairing**
```
Generate PIN (6-digit) → Middleware API → Other device claims PIN →
SSH key exchange (port 44444) → Keys added to authorized_keys → Paired
```

**3. Communication**
```
┌─────────────────────────────────────────────────────┐
│  App Layer (Clipboard, File Transfer, Remote, etc.) │
└───────────────────┬─────────────────────────────────┘
                    │
         ┌──────────┴──────────┐
         │                     │
   ┌─────▼─────┐       ┌──────▼──────┐
   │   SSH     │       │  Unified    │
   │  (Port 22)│       │ Protocol    │
   │  SFTP     │       │ (Port 55555)│
   └──────────┘       └──────┬──────┘
         │                    │
         └───────────────────┘
                    │
         ┌──────────▼──────────┐
         │   SOCKS5 Proxy      │
         │   (Port 1055)       │
         │   Tailscale Mesh    │
         └─────────────────────┘
```

### Ports & Protocols

| Port | Protocol | Purpose |
|------|----------|---------|
| 1055 | SOCKS5 | Tailscale userspace proxy (localhost only) |
| 22 | SSH/SFTP | Standard SSH (Windows/Linux) |
| 2222 | SSH/SFTP | Android SSH server |
| 44444 | TCP | SSH key exchange handshake |
| 55555 | Custom TCP | Unified protocol (clipboard, remote, audio, monitoring) |

### Platform-Specific Notes

**Android:**
- Uses Go `tsnet` library for userspace networking (no TUN device)
- All connections must route through SOCKS5 proxy at `127.0.0.1:1055`
- SSH runs on port 2222 (not 22)
- Files saved to `/storage/emulated/0/Download/`
- Audio capture: microphone only (no system loopback)

**Windows:**
- Full WASAPI loopback for system audio capture
- VB-Audio Cable for virtual microphone routing
- OpenSSH server auto-install with admin rights
- Best overall support

**Linux:**
- Uses system OpenSSH server
- Audio requires manual PulseAudio/PipeWire routing to `EchoLink_Sink`
- Telemetry reads from `/proc` and `/sys/class/thermal`
- System commands via `loginctl` and `systemctl`

---

## 📖 Documentation

- **[Quickstart Guide](docs/SETUP.md)** - Get up and running in 10 minutes
- **[Developer Guide](docs/DEVELOPER.md)** - Architecture deep-dive, building, debugging
- **[Contributing](docs/CONTRIBUTING.md)** - How to contribute, coding standards

---

## 🚀 Quick Start

```bash
# Clone
git clone https://github.com/uganthan2005/EchoLink
cd EchoLink

# Build (requires .NET 10 SDK)
dotnet build

# Run
dotnet run --project EchoLink.csproj
```

See [docs/SETUP.md](docs/SETUP.md) for detailed setup instructions, Headscale configuration, and first-time pairing guide.

---

## 🤝 Contributing

Contributions welcome! See [docs/CONTRIBUTING.md](docs/CONTRIBUTING.md) for:
- Development setup
- Coding standards
- Pull request process
- Areas that need help (Linux audio, macOS support, iOS client)

---

## 📄 License

MIT License - see [LICENSE](LICENSE) file.

---

## 🏆 Built With

- [Avalonia UI](https://avaloniaui.net/) - Cross-platform XAML UI
- [Tailscale](https://tailscale.com/) - Mesh networking
- [Headscale](https://github.com/juanfont/headscale) - Self-hosted control server
- [SSH.NET](https://github.com/sshnet/SSH.NET) - SSH/SFTP library
- [Opus](https://opus-codec.org/) - Low-latency audio codec
- [NAudio](https://github.com/naudio/NAudio) - Windows audio I/O

---

<div align="center">


Questions? [Open an issue](https://github.com/uganthan2005/EchoLink/issues)

</div>
