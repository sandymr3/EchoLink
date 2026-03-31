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
   - Remote control commands (mouse, keyboard, system actions)
   - Audio streaming
   - System monitoring
   - Custom TCP: `[Type:1][Length:4][Payload:N]`

### Device Identity (NodeId-Based)

As of 2026-03-31, EchoLink uses **NodeId** (persistent) instead of IP address (ephemeral) for device identity:

- **Settings Storage:** `ApprovedGuests` dictionary keyed by NodeId
- **Deduplication:** Groups devices by NodeId, not Name+UserId
- **IP Tracking:** Automatic update when device IP changes
- **Legacy Support:** Fallback to old `PeerUsernames` for backward compatibility

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
│   ├── ApprovedGuest.cs     # Trusted guest device info
│   └── ...
│
├── ViewModels/
│   ├── DashboardViewModel.cs      # Device list, pairing
│   ├── FileTransferViewModel.cs   # SFTP browser
│   ├── RemoteControlViewModel.cs  # Mouse, system actions, click buttons
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
│   │   ├── SettingsService.cs        # Persistent config (NodeId-based)
│   │   ├── DeviceDiscoveryService.cs # Device discovery, IP tracking
│   │   └── TrustStoreService.cs      # Guest approval by NodeId
│   │
│   ├── Mesh/
│   │   └── TailScaleService.cs       # Daemon management, deduplication
│   │
│   ├── Auth/
│   │   ├── AuthService.cs            # OIDC login
│   │   └── MiddlewareClient.cs       # PIN API client
│   │
│   ├── Ssh/
│   │   ├── SshPairingService.cs      # Key exchange (NodeId-based)
│   │   ├── SftpService.cs            # File transfer
│   │   └── SshTunnelService.cs       # Port forwarding
│   │
│   ├── UnifiedProtocol/
│   │   ├── UnifiedProtocolService.cs # TCP server (55555)
│   │   ├── UnifiedProtocolClient.cs  # TCP client
│   │   ├── UnifiedProtocolClientExtensions.cs # Send helpers
│   │   └── UnifiedMessageType.cs     # Message enum
│   │
│   ├── Clipboard/
│   │   └── ClipboardSyncService.cs   # MirrorClip engine
│   │
│   ├── Audio/
│   │   └── AudioStreamingService.cs  # Opus encode/decode
│   │
│   ├── RemoteControl/
│   │   ├── RemoteControlService.cs   # Mouse/system commands
│   │   ├── MouseControlService.cs    # Mouse event handling
│   │   └── KeyboardControlService.cs # Keyboard events
│   │
│   ├── SystemMonitor/
│   │   └── SystemMonitorService.cs   # Telemetry collector
│   │
│   └── Macro/
│       └── MacroService.cs           # Macro sync/execution
│
├── Go/ (Android native bridge)
│   ├── main.go              # tsnet server, exports
│   └── ...
│
├── EchoLink.Android/        # Android-specific C#
├── EchoLink.Windows/        # Windows-specific C#
└── EchoLink.Linux/          # Linux-specific C#
```

---

## Building

### Windows/Linux/macOS

```bash
dotnet build
dotnet run --project EchoLink.csproj
```

### Android

```bash
cd Go
gomobile init
gomobile bind -target android -o ../EchoLink.Android/libecholink.aar ./...

# Then build Android project in Visual Studio
```

---

## Debugging

### Visual Studio / VS Code

1. Set breakpoints in C# code
2. `F5` to start debugging
3. Console output shows service logs

### Android (Go Bridge)

```bash
adb logcat | grep EchoLink-Go
```

### Unified Protocol Messages

Enable debug logging in `LoggingService` to see message types:
```
[Unified] Sending MouseMove (dx=10, dy=5)
[Unified] Received ClipboardSync (45 chars)
```

---

## Platform-Specific Development

### Android

- **All TCP connections MUST use SOCKS5 proxy** at `127.0.0.1:1055`
- SSH runs on port **2222**, not 22
- Go bridge handles `tsnet`, SSH server, audio capture
- Files saved to `/storage/emulated/0/Download/`

### Windows

- Full WASAPI loopback for system audio
- VB-Audio Cable for virtual microphone routing
- OpenSSH auto-install with admin rights

### Linux

- Uses system OpenSSH server
- Audio requires PulseAudio/PipeWire routing
- Telemetry from `/proc` and `/sys/class/thermal`

---

## Key Components

### DeviceDiscoveryService

Centralized device discovery and caching:
- Fetches devices from Tailscale
- Filters by UserId (Ecosystem) or TrustStore (Guests)
- Tracks IP changes via NodeId
- Injects offline approved guests (phantoms)
- Fires `DeviceListChanged` event for UI updates

### UnifiedProtocolService

TCP server on port 55555:
- Accepts connections from peers
- Dispatches messages to registered handlers
- Message format: `[Type:1][Length:4][Payload:N]`

### ClipboardSyncService

Event-driven clipboard sync:
- Monitors local clipboard changes
- Broadcasts to eligible peers via Unified Protocol
- Applies remote clipboard to local device
- Per-peer failure tracking with exponential backoff

### SshPairingService

SSH key exchange on port 44444:
- Listens for pairing requests
- Exchanges public keys
- Saves to `ApprovedGuests` by NodeId
- Manages `authorized_keys` file

---

## Testing

### Manual Testing Checklist

- [ ] Login with Google OIDC
- [ ] Pair two devices via PIN
- [ ] Clipboard sync (both directions)
- [ ] File transfer (upload/download)
- [ ] Remote mouse control + click buttons
- [ ] System monitor shows correct values
- [ ] Audio streaming works
- [ ] Macros execute on target

### Automated Tests

```bash
dotnet test
```

(Tests are minimal; more needed!)

---

## Code Style

### Naming Conventions

```csharp
public class MyClass { }           // Classes: PascalCase
public void MyMethod() { }         // Methods: PascalCase
private string _privateField;      // Fields: _camelCase
public string PublicProperty { get; set; }  // Properties: PascalCase
```

### Async/Await

- Always use `Async` suffix for async methods
- Pass `CancellationToken` for long-running operations
- Use `ConfigureAwait(false)` in library code

### Logging

```csharp
_log.Info($"[ServiceName] Operation completed");
_log.Error($"[ServiceName] Failed: {ex.Message}");
```

### MVVM Pattern

- ViewModels inherit from `ViewModelBase`
- Use `ObservableProperty` attribute for properties
- Commands use `[RelayCommand]` attribute

---

## Recent Changes (2026-03-31)

### NodeId Migration

- **Before:** `PeerUsernames` dictionary keyed by IP
- **After:** `ApprovedGuests` dictionary keyed by NodeId
- **Benefit:** Devices maintain identity across IP changes

### Mouse Click Buttons

- Added Left/Right/Middle click buttons to RemoteControl
- Trackpad sends left-click on touch
- Uses `UnifiedProtocolClient.SendMouseClickAsync()`

### Refresh Command

- All feature ViewModels now have `RefreshCommand`
- Triggers fresh device discovery from Tailscale
- Preserves selected device after refresh

### Clipboard Fix

- Fixed PC-to-Phone clipboard sync
- Was checking old `PeerUsernames` (empty after migration)
- Now checks `ApprovedGuests` first

---

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for:
- How to fork and clone
- Branch naming conventions
- Pull request process
- Code review guidelines

---

**Questions?** Open an issue or ask in Discussions!
