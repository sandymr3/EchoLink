# Developer Guide

Deep dive into EchoLink's architecture, building, debugging, and platform-specific quirks. Read this before contributing.

---

## Table of Contents

1. [Architecture Overview](#architecture-overview)
2. [Project Structure](#project-structure)
3. [Building](#building)
4. [Debugging](#debugging)
5. [Platform-Specific Development](#platform-specific-development)
6. [Key Components](#key-components)
7. [Testing](#testing)
8. [Code Style](#code-style)

---

## Architecture Overview

EchoLink has three layers:

```
┌─────────────────────────────────────────────────┐
│              UI Layer (Avalonia)                │
│  Dashboard, FileTransfer, RemoteControl, etc.   │
└───────────────────┬─────────────────────────────┘
                    │ (MVVM via CommunityToolkit)
┌───────────────────▼─────────────────────────────┐
│              Service Layer                      │
│  Tailscale, SSH, UnifiedProtocol, Clipboard...  │
└───────────────────┬─────────────────────────────┘
                    │
         ┌──────────┴──────────┐
         │                     │
┌────────▼────────┐   ┌────────▼────────┐
│  Tailscale Mesh │   │  Go Native      │
│  (SOCKS5:1055)  │   │  Bridge (Android)│
└─────────────────┘   └─────────────────┘
```

### Communication Model

**Non-Android:**
```
App → SOCKS5 (1055) → Tailscale → Remote Device
```

**Android:**
```
App → SOCKS5 (1055) → Go tsnet → Tailscale → Remote Device
           ↑
    All connections MUST use this proxy
    (no direct TCP to 100.x.y.z)
```

### Two Communication Channels

1. **SSH/SFTP (Port 22/2222)**
   - File transfers
   - Initial key exchange
   - Android uses port 2222

2. **Unified Protocol (Port 55555)**
   - Clipboard sync
   - Remote control commands
   - Audio streaming
   - System monitoring
   - Custom TCP: `[Type:1][Length:4][Payload:N]`

---

## Project Structure

```
EchoLink/
├── EchoLink.csproj          # Main project (.NET 10)
├── App.axaml                # Application root
├── ViewLocator.cs           # ViewModel → View resolution
│
├── Models/
│   ├── Device.cs            # Peer device representation
│   ├── TelemetrySnapshot.cs # CPU/RAM/disk data
│   └── ...
│
├── ViewModels/
│   ├── DashboardViewModel.cs      # Device list, pairing
│   ├── FileTransferViewModel.cs   # SFTP browser
│   ├── RemoteControlViewModel.cs  # Mouse, system actions
│   ├── ClipboardViewModel.cs      # Clipboard hub
│   ├── SystemMonitorViewModel.cs  # Telemetry display
│   ├── MacrosViewModel.cs         # Macro buttons
│   └── MainWindowViewModel.cs     # Shell VM
│
├── Views/
│   ├── MainWindow.axaml
│   ├── MainView.axaml
│   ├── DashboardView.axaml
│   ├── FileTransferView.axaml
│   └── ... (one per VM)
│
├── Services/
│   ├── Core/
│   │   ├── NetworkService.cs         # SOCKS5 connections
│   │   └── SettingsService.cs        # Persistent config
│   │
│   ├── Mesh/
│   │   └── TailScaleService.cs       # Daemon management
│   │
│   ├── Auth/
│   │   ├── AuthService.cs            # OIDC login
│   │   └── MiddlewareClient.cs       # PIN API client
│   │
│   ├── Ssh/
│   │   ├── SshPairingService.cs      # Key exchange
│   │   ├── SftpService.cs            # File transfer
│   │   └── SshTunnelService.cs       # Port forwarding
│   │
│   ├── UnifiedProtocol/
│   │   ├── UnifiedProtocolService.cs # TCP server (55555)
│   │   ├── UnifiedProtocolClient.cs  # TCP client
│   │   └── UnifiedMessageType.cs     # Message enum
│   │
│   ├── Clipboard/
│   │   └── ClipboardSyncService.cs   # MirrorClip engine
│   │
│   ├── Audio/
│   │   └── AudioStreamingService.cs  # Opus encode/decode
│   │
│   ├── RemoteControl/
│   │   └── RemoteControlService.cs   # Mouse/system commands
│   │
│   ├── SystemMonitor/
│   │   └── SystemMonitorService.cs   # Telemetry collector
│   │
│   └── Macro/
│       └── MacroService.cs           # Macro sync/execution
│
├── Go/ (Android native bridge)
│   ├── main.go              # tsnet server, exports
│   ├── sftp.go              # SFTP handler
│   └── audio.go             # Audio capture
│
├── EchoLink.Android/        # Android-specific C#
├── EchoLink.Windows/        # Windows-specific C#
└── EchoLink.Linux/          # Linux-specific C#
```

---

## Building

### Desktop (Windows/Linux/macOS)

```bash
# Debug
dotnet build

# Release
dotnet build -c Release

# Run
dotnet run --project EchoLink.csproj

# Self-contained publish
dotnet publish -c Release -r win-x64 --self-contained
dotnet publish -c Release -r linux-x64 --self-contained
```

### Android

**Prerequisites:**
- Go 1.20+
- Android NDK r25+
- `gomobile` installed

```bash
cd Go
gomobile init
gomobile bind -target android -o ../EchoLink.Android/libecholink.aar ./...

# Build Android app
cd ../EchoLink.Android
dotnet build -c Release
```

**Troubleshooting Android builds:**

If you get `undefined: tsnet`:
```bash
go get tailscale.com/tsnet
go mod tidy
```

If you get Cgo errors:
```bash
export CGO_ENABLED=1
export ANDROID_NDK_HOME=/path/to/ndk
```

---

## Debugging

### Desktop

**Visual Studio / Rider:**
- Standard .NET debugging works
- Set breakpoints in ViewModels/Services
- Attach to running process if needed

**VS Code:**
```json
// .vscode/launch.json
{
  "name": "EchoLink",
  "type": "coreclr",
  "request": "launch",
  "program": "${workspaceFolder}/bin/Debug/net10.0/EchoLink.dll",
  "args": [],
  "cwd": "${workspaceFolder}",
  "console": "internalConsole"
}
```

### Android

**Attach debugger:**
```bash
# Start app
adb shell am start -n com.echolink.app/.MainActivity

# Attach debugger
adb forward tcp:55555 tcp:55555
dotnet attach <pid>
```

**View Go logs:**
```bash
adb logcat | grep -i echolink
```

**Rebuild Go bridge:**
```bash
cd Go
gomobile bind -target android -v -o ../EchoLink.Android/libecholink.aar ./...
```

---

## Platform-Specific Development

### Windows

**SSH Server Installation:**
```csharp
// Services/Ssh/SshInstallationService.cs
// Auto-installs OpenSSH with admin rights
// Patches sshd_config to use ~/.ssh/authorized_keys
```

**Audio:**
- Uses `WasapiLoopbackCapture` (NAudio)
- VB-Audio Cable for virtual mic
- Driver INF in `Assets/VirtualAudio/`

**Firewall Rules:**
```powershell
# Added automatically during install
netsh advfirewall firewall add rule name="EchoLink" dir=in action=allow program="C:\path\to\echolink.exe" enable=yes
```

### Linux

**SSH Server:**
```bash
# Uses system openssh-server
sudo systemctl enable sshd
sudo systemctl start sshd

# Ensure ~/.ssh directory exists
mkdir -p ~/.ssh
chmod 700 ~/.ssh
```

**Audio (PulseAudio):**
```bash
# Create virtual sink
pactl load-module module-null-sink sink_name=EchoLink_Sink

# Route app audio to EchoLink_Sink
pactl move-sink-input <app_id> EchoLink_Sink

# Capture from monitor
parec -d EchoLink_Sink.monitor | opusenc - audio.opus
```

**Telemetry Sources:**
- CPU: `/proc/stat`
- RAM: `/proc/meminfo`
- Disk: `df`
- Temperature: `/sys/class/thermal/thermal_zone*/temp`
- Battery: `/sys/class/power_supply/BAT*/uevent`

**System Commands:**
```bash
# Lock
loginctl lock-session

# Restart
systemctl reboot

# Shutdown
systemctl poweroff
```

### Android

**Go Bridge Exports:**
```go
//export StartEchoLinkNode
func StartEchoLinkNode(configDir, authKey, hostname, localIp, isEphemeral string) int

//export GetBackendState  // "Stopped", "Starting", "NeedsLogin", "Running", "Error"
//export GetTailscaleIp
//export GetPeerListJson
//export SetTempSshPassword
```

**C# Interop:**
```csharp
// Must use CharSet.Ansi!
[DllImport("libecholink", CharSet = CharSet.Ansi)]
private static extern IntPtr StartEchoLinkNode(
    string configDir,
    string authKey,
    string hostname,
    string localIp,
    string isEphemeral);
```

**SOCKS5 is Mandatory:**
```csharp
// WRONG - will fail on Android
var client = new TcpClient("100.115.42.17", 22);

// CORRECT - routes through proxy
var client = await networkService.ConnectViaSocks5Async("100.115.42.17", 22);
```

**File Paths:**
```csharp
// Android uses scoped storage
var downloadPath = Android.App.Application.Context
    .GetExternalFilesDir(null).AbsolutePath;
// → /storage/emulated/0/Android/data/com.echolink.app/files/
```

---

## Key Components

### TailscaleService

Manages the embedded `tailscaled` daemon.

```csharp
// Start daemon
await tailScaleService.StartAsync(authKey);

// Get peers
var peers = await tailScaleService.GetPeersAsync();

// Check status
var status = await tailScaleService.GetNetworkStatusAsync();
```

**Key points:**
- Runs in userspace mode (`--tun=userspace-networking`)
- SOCKS5 proxy on `localhost:1055`
- State file: `~/.config/echolink/tailscaled.state`

### NetworkService

Universal SOCKS5 connection handler.

```csharp
// Connect to any Tailscale IP
var stream = await networkService.ConnectViaSocks5Async(
    "100.115.42.17",  // target IP
    55555             // port
);
```

**SOCKS5 handshake:**
```
C: CONNECT 100.115.42.17:55555
S: OK
C: [data]
```

### UnifiedProtocolService

Custom TCP server for all non-SSH features.

**Message format:**
```
[Type: 1 byte][Length: 4 bytes big-endian][Payload: N bytes]
```

**Example: MouseMove**
```
Type: 0x01
Length: 0x00000004
Payload: [dx:int16][dy:int16]
```

**Register handler:**
```csharp
unifiedProtocolService.RegisterHandler(
    UnifiedMessageType.MouseMove,
    async (stream, payload) => {
        var dx = BitConverter.ToInt16(payload, 0);
        var dy = BitConverter.ToInt16(payload, 2);
        await MoveMouseAsync(dx, dy);
    }
);
```

### ClipboardSyncService

Three modes:

1. **MirrorClip** (auto-broadcast)
   ```csharp
   clipboardSyncService.EnableMirrorClip();
   // Monitors clipboard, broadcasts changes to all paired devices
   ```

2. **SnapShare** (manual push)
   ```csharp
   await clipboardSyncService.PushClipboardAsync(targetDevices);
   ```

3. **GhostPaste** (remote apply)
   ```csharp
   await clipboardSyncService.ApplyRemoteClipboardAsync(device);
   // Applies clipboard without showing it locally
   ```

**Journal for reliability:**
```csharp
// Failed sends are journaled and retried with exponential backoff
// Journal stored in ~/.config/echolink/clipboard_journal.json
```

### AudioStreamingService

**Encoder config:**
```csharp
var encoder = OpusEncoder.Create(48000, 1, OPUS_APPLICATION_RESTRICTED_LOWDELAY);
encoder.Bitrate = 24000; // 24 kbps
_frameSize = 960; // 20ms @ 48kHz
```

**Capture (Windows):**
```csharp
var capture = new WasapiLoopbackCapture();
capture.DataAvailable += (s, e) => {
    var pcm = ConvertFloat32ToInt16Mono(e.Buffer);
    var encoded = encoder.Encode(pcm);
    SendAudioFrame(encoded);
};
```

**Playback:**
```csharp
var player = new WaveOutEvent();
player.Init(new RawSourceWaveStream(decoderStream, waveFormat));
player.Play();
```

---

## Testing

### Unit Tests

```bash
# Run all tests
dotnet test

# Run specific test class
dotnet test --filter "FullyQualifiedName~ClipboardSyncServiceTests"

# Coverage
dotnet test /p:CollectCoverage=true /p:CoverletOutputFormat=cobertura
```

### Integration Tests

**Test pairing flow:**
```csharp
[Fact]
public async Task PairingFlow_CompletesSuccessfully()
{
    // Arrange
    var deviceA = new TestDevice();
    var deviceB = new TestDevice();
    
    // Act
    var pin = await deviceA.GeneratePairingPinAsync();
    await deviceB.ClaimPinAsync(pin);
    
    // Assert
    Assert.True(deviceA.IsPaired(deviceB));
    Assert.True(deviceB.IsPaired(deviceA));
}
```

### Manual Testing Checklist

Before PR:
- [ ] Login works (Google OIDC)
- [ ] Pairing works (PIN and direct)
- [ ] Clipboard sync works (all three modes)
- [ ] File transfer works (upload + download)
- [ ] Remote control works (mouse + system actions)
- [ ] Audio streaming works (both directions)
- [ ] System monitor shows data
- [ ] Macros execute
- [ ] Android build runs (if changes affect Android)

---

## Code Style

### C# Conventions

**Naming:**
- Classes: `PascalCase`
- Methods: `PascalCase`
- Private fields: `_camelCase`
- Interfaces: `IPascalCase`

**Async:**
- Always use `Async` suffix
- Return `Task` or `Task<T>`, never `void`
- Use `ConfigureAwait(false)` in library code

```csharp
public async Task<Device> GetDeviceAsync(string id)
{
    // ...
}
```

**Dependency Injection:**
- Constructor injection only
- Register services in `App.axaml.cs`

```csharp
public class DashboardViewModel
{
    private readonly IClipboardSyncService _clipboard;
    
    public DashboardViewModel(IClipboardSyncService clipboard)
    {
        _clipboard = clipboard;
    }
}
```

### Git Workflow

```bash
# Create branch
git checkout -b feature/your-feature-name

# Commit (conventional commits)
git commit -m "feat: add macro sync support"
git commit -m "fix: android path resolution for SFTP"
git commit -m "docs: update SETUP.md with Headscale guide"

# Push
git push origin feature/your-feature-name
```

**Commit types:**
- `feat:` New feature
- `fix:` Bug fix
- `docs:` Documentation
- `refactor:` Code reorganization
- `test:` Tests
- `chore:` Build/config changes

---

## Common Pitfalls

### 1. Forgetting SOCKS5 on Android

```csharp
// ❌ This will fail on Android
var client = new TcpClient("100.115.42.17", 22);

// ✅ Use NetworkService
var client = await _networkService.ConnectViaSocks5Async("100.115.42.17", 22);
```

### 2. Wrong CharSet in P/Invoke

```csharp
// ❌ Strings will be corrupted
[DllImport("libecholink")]
private static extern IntPtr StartEchoLinkNode(string configDir);

// ✅ Specify CharSet.Ansi
[DllImport("libecholink", CharSet = CharSet.Ansi)]
private static extern IntPtr StartEchoLinkNode(string configDir);
```

### 3. Using SFTP In-Memory Handler on Android

```csharp
// ❌ Files will be discarded
var sftp = new SftpClient(conn);
sftp.Connect();
using var remoteStream = sftp.Open("/path", FileMode.Create);

// ✅ Use filesystem-backed SFTP
var sftp = new SftpClient(conn);
sftp.Connect();
sftp.UploadFile(localStream, "/path"); // Uses real filesystem
```

### 4. Not Handling Android Paths

```csharp
// ❌ Returns null on Android
var path = file.TryGetLocalPath();

// ✅ Use stream API
await using var stream = await file.OpenReadAsync();
await sftp.UploadStreamAsync(stream, remotePath);
```

---

## Resources

- [Avalonia Documentation](https://docs.avaloniaui.net/)
- [Tailscale Developer Docs](https://tailscale.com/kb/)
- [Headscale Documentation](https://headscale.net/)
- [SSH.NET Examples](https://github.com/sshnet/SSH.NET/tree/develop/src/Renci.SshNet.Tests)
- [Opus Codec Docs](https://opus-codec.org/docs/)

---

**Questions?** Check existing issues or open a new one with the `[dev]` label.
