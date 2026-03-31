# Quickstart Guide

Get EchoLink running in about 10 minutes. This guide covers:
1. Building the application
2. Setting up Headscale (self-hosted Tailscale)
3. First-time login and pairing
4. Using core features

---

## Prerequisites

- **.NET 10 SDK** - [Download](https://dotnet.microsoft.com/download)
- **Git**
- **A machine to run Headscale** (can be the same as your EchoLink machine)
- **Android NDK + Go** (only if building Android app)

---

## 1. Build EchoLink

```bash
# Clone the repo
git clone https://github.com/uganthan2005/EchoLink
cd EchoLink

# Build
dotnet build

# Run (Linux/macOS)
dotnet run --project EchoLink.csproj

# Run (Windows)
# Open in Visual Studio or run:
dotnet run --project EchoLink.csproj
```

**Android (optional):**
```bash
# Requires Go 1.20+ and Android NDK
cd Go
gomobile init
gomobile bind -target android -o ../EchoLink.Android/libecholink.aar ./...
```

---

## 2. Set Up Headscale

EchoLink uses a self-hosted Tailscale control server. You have two options:

### Option A: Use the Hosted Server (Easiest)

The project runs a Headscale instance at `https://control.echo-link.app`. Skip to Step 3.

### Option B: Self-Host Your Own (Recommended for Production)

**Using Docker:**

```bash
# Create docker-compose.yml
version: '3.8'
services:
  headscale:
    image: headscale/headscale:latest
    ports:
      - "8080:8080"
      - "9090:9090"
    volumes:
      - ./data:/var/lib/headscale
      - ./config:/etc/headscale
    command: headscale serve
    restart: unless-stopped

  headscale-ui:
    image: ghcr.io/gurucomputing/headscale-ui:latest
    ports:
      - "9091:80"
    environment:
      - HEADSCALE_URL=http://headscale:8080
    restart: unless-stopped
```

```bash
# Start
docker-compose up -d

# Create your first user
docker exec -it <container_id> headscale users create yourname

# Create a pre-auth key
docker exec -it <container_id> headscale preauthkeys create -u yourname -r 1
```

**Configure EchoLink to use your Headscale:**

Edit `Services/Auth/MiddlewareClient.cs` and update:
```csharp
private const string HeadscaleUrl = "https://your-headscale-url.com";
```

---

## 3. First Login

1. **Launch EchoLink**
2. Click **Login with Google**
3. Complete the OAuth flow
4. You'll see your Tailscale IP (100.x.y.z) and status "Connected"

**What just happened:**
- Google OIDC verified your identity
- Middleware exchanged your JWT for a Tailscale pre-auth key
- `tailscaled` daemon started in userspace networking mode
- SOCKS5 proxy is running on `localhost:1055`

---

## 4. Pair Your First Device

You need at least two devices logged in to pair them.

### Method 1: Generate PIN (Recommended)

**On Device A (the one you want to connect TO):**

1. Go to **Dashboard** tab
2. Click **Generate Pairing PIN**
3. A 6-digit PIN appears (expires in 10 minutes)
4. Share this PIN with Device B (text, QR code, whatever)

**On Device B (the one initiating):**

1. Go to **Dashboard** tab
2. Click **Enter Pairing PIN**
3. Type the 6-digit PIN
4. Click **Connect**

**What happens next:**
- Device B contacts the middleware API to claim the PIN
- Middleware returns Device A's IP address and public SSH key
- Device B connects to Device A on port 44444
- SSH keys are exchanged and added to `~/.ssh/authorized_keys`
- Both devices save each other to their paired list

---

## 5. Try Core Features

### Clipboard Sync

**Auto-sync (MirrorClip):**
1. Enable **MirrorClip** in Settings → EchoBoard
2. Copy something on Device A
3. Wait ~1 second
4. Paste on Device B (it's already there!)

**Manual push (SnapShare):**
1. Copy text on Device A
2. Click **SnapShare** button
3. Select target device(s)
4. Clipboard is pushed immediately

**Remote apply (GhostPaste):**
1. Copy text on Device A
2. In Dashboard, click the clipboard icon next to Device B
3. Select **GhostPaste**
4. Text is applied to Device B's clipboard (without showing Device A's clipboard content locally)

### File Transfer

1. Go to **File Transfer** tab
2. Select a paired device from dropdown
3. Browse their filesystem (SFTP)
4. **Download:** Drag files from remote pane to local pane
5. **Upload:** Drag files from local pane to remote pane

**Android note:** Files are saved to `/storage/emulated/0/Download/EchoLink/`

### Remote Control

1. Go to **Remote Control** tab
2. Select a paired device
3. **Mouse:** Move your mouse in the trackpad area—remote cursor moves
4. **Click Buttons:** Use 🖱️ Left, 🖱️ Middle, 🖱️ Right buttons for mouse clicks
5. **System Actions:** Click Lock, Restart, or Shutdown

**Linux note:** Requires `loginctl` for lock command. May need polkit configuration.

### System Monitor

1. Go to **System Monitor** tab
2. Select a paired device
3. View real-time CPU, RAM, disk usage
4. Linux/Android also show battery and temperature

Data refreshes every 10 seconds automatically. Click **↻ Refresh** to update device list.

### Audio Streaming

**Use Phone as Microphone:**

1. On Windows PC: Install [VB-Audio Cable](https://vb-audio.com/Cable/)
2. In EchoLink Settings, click **Install Virtual Audio Driver**
3. On Phone: Go to Remote Control → Audio
4. Select **Microphone** mode
5. On PC: Set recording device to "CABLE Output"

**Stream PC Audio to Phone:**

1. On PC: Go to Remote Control → Audio
2. Select **System Audio** mode
3. On Phone: Audio plays through speakers

**Latency:** ~50-100ms on good networks. Not studio quality, but usable for voice.

### Macros

1. Go to **Macros** tab
2. Default macros appear (Lock, Restart, Sleep on Windows; Lock, Update on Linux)
3. Click a macro button to execute on all paired devices
4. **Create custom macro:** Edit `~/EchoLink/Macros/macro.json`

**Example custom macro:**
```json
{
  "name": "Restart Audio Service",
  "icon": "🔊",
  "commands": [
    {
      "os": "windows",
      "command": "net stop Audiosrv && net start Audiosrv"
    },
    {
      "os": "linux",
      "command": "systemctl --user restart pulseaudio"
    }
  ]
}
```

Save the file—EchoLink auto-reloads macros instantly.

---

## 6. Guest Access (Optional)

Want to give someone temporary access?

1. Go to **Dashboard**
2. Click **Generate Guest PIN**
3. Share the PIN
4. Guest device pairs normally but gets tagged as `guest`
5. Revoke access anytime by removing them from paired list

Guest PINs expire after 24 hours by default.

---

## Troubleshooting

### "Cannot connect to device"

- Check both devices show "Connected" in Dashboard
- Verify Tailscale IPs are reachable: `ping 100.x.y.z`
- Check firewall: ports 22, 2222 (Android), 44444, 55555 must be open on Tailscale interface

### "SSH connection refused"

- Android uses port 2222, not 22
- Windows: Ensure OpenSSH Server is running (`services.msc` → "OpenSSH Server")
- Linux: `sudo systemctl status sshd`

### "Audio not working"

- Windows: Install VB-Audio Cable, set as default recording device
- Linux: Route PulseAudio output to `EchoLink_Sink` monitor
- Android: Only microphone works (no system loopback)

### "Clipboard not syncing"

- Check MirrorClip is enabled in Settings
- Verify both devices are paired (not just connected)
- Check firewall: port 55555 must be open

### "Headscale won't connect"

- Check `~/.config/headscale/config.yaml` for correct listen address
- Ensure port 8080 is accessible
- Verify pre-auth keys haven't expired

### "Device shows as offline"

- Click **↻ Refresh** button in the feature section
- Device list auto-refreshes but may need manual trigger
- Selected device is preserved after refresh

### "Duplicate devices in list"

- This was a known issue with IP-based identity
- Latest version uses NodeId-based identity (no duplicates)
- If you see duplicates, restart both devices to clear cache

---

## Next Steps

- **[Developer Guide](DEVELOPER.md)** - Architecture, building, debugging
- **[Contributing](CONTRIBUTING.md)** - How to contribute code
- **[GitHub Issues](https://github.com/uganthan2005/EchoLink/issues)** - Report bugs, request features

---

**Still stuck?** Open an issue with:
- Your OS and .NET version
- Headscale config (redact secrets)
- Error messages from console
- What you were trying to do
