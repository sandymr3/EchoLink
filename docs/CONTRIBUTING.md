# Contributing to EchoLink

Thanks for wanting to contribute! This guide covers how to contribute code, report issues, and join the project.

---

## Quick Links

- **[Quickstart Guide](SETUP.md)** - Get EchoLink running
- **[Developer Guide](DEVELOPER.md)** - Architecture and building
- **[Issues](https://github.com/uganthan2005/EchoLink/issues)** - Report bugs, request features
- **[Discussions](https://github.com/uganthan2005/EchoLink/discussions)** - Questions, ideas, show-and-tell

---

## How to Contribute

### 1. Find Something to Work On

Check these labels for good starting points:

- [`good first issue`](https://github.com/uganthan2005/EchoLink/issues?q=is%3Aissue+label%3A%22good+first+issue%22) - Beginner-friendly
- [`help wanted`](https://github.com/uganthan2005/EchoLink/issues?q=is%3Aissue+label%3A%22help+wanted%22) - Need contributors
- [`bug`](https://github.com/uganthan2005/EchoLink/issues?q=is%3Aissue+label%3Abug) - Bugs to fix
- [`enhancement`](https://github.com/uganthan2005/EchoLink/issues?q=is%3Aissue+label%3Aenhancement) - Feature requests

**High-priority areas:**
- 🐧 Linux audio capture bridge (PulseAudio/PipeWire integration)
- 🍎 macOS support (audio, remote control)
- 📱 iOS client (entirely new platform)
- 📝 Documentation improvements
- 🧪 Unit tests for Services

### 2. Fork and Clone

```bash
# Fork on GitHub, then:
git clone https://github.com/YOUR_USERNAME/EchoLink
cd EchoLink
git remote add upstream https://github.com/uganthan2005/EchoLink
```

### 3. Create a Branch

```bash
git checkout -b feature/your-feature-name
# or
git checkout -b fix/issue-123
```

**Branch naming:**
- `feat/` - New features
- `fix/` - Bug fixes
- `docs/` - Documentation
- `refactor/` - Code reorganization
- `test/` - Tests

### 4. Make Your Changes

**Before coding:**
1. Read [Developer Guide](DEVELOPER.md) for architecture
2. Check existing code for patterns
3. If adding a feature, think about tests

**Code style:**
- Follow existing patterns in the codebase
- Use meaningful variable names
- Add comments for complex logic (not obvious stuff)
- Keep methods small and focused

**Platform considerations:**
- Test on your target platform
- Consider cross-platform impact
- Android changes require Go bridge review

### 5. Test Locally

```bash
# Build
dotnet build

# Run tests (if available)
dotnet test

# Run the app
dotnet run --project EchoLink.csproj
```

**Manual testing checklist:**
- [ ] Login works
- [ ] Pairing works
- [ ] Your feature works as expected
- [ ] No console errors
- [ ] Doesn't break existing features

### 6. Commit Your Changes

```bash
git add .
git commit -m "type: description"
```

**Commit message format:**
```
feat: add mouse click buttons to remote control
fix: clipboard sync failing PC-to-Phone
docs: update README with latest features
refactor: migrate device identity to NodeId
```

**Types:**
- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation only
- `refactor:` - Code refactoring
- `test:` - Adding tests
- `chore:` - Build/config changes

### 7. Push and Create Pull Request

```bash
git push origin feature/your-feature-name
```

Then on GitHub:
1. Go to your fork
2. Click "Compare & pull request"
3. Fill in the template
4. Link related issues
5. Submit!

### 8. Code Review

- Maintainers will review your PR
- Address feedback by pushing new commits
- Once approved, it will be merged

---

## Development Setup

### Prerequisites

- **.NET 10 SDK** - [Download](https://dotnet.microsoft.com/download)
- **Git**
- **VS Code** or **Visual Studio** (Community Edition is free)
- **Go 1.20+** (for Android development)
- **Android NDK** (for Android development)

### IDE Setup

**VS Code:**
1. Install C# extension
2. Install Avalonia Template extension
3. Open folder in VS Code

**Visual Studio:**
1. Install ".NET desktop development" workload
2. Install "Mobile development with .NET" (for Android)
3. Open `echolink.sln`

---

## Coding Standards

### C# Conventions

```csharp
// Use var for implicit typing
var devices = GetDevices();

// Use string interpolation
_log.Info($"[Service] Connected to {device.Name}");

// Use async/await properly
public async Task<Device> GetDeviceAsync(string id) { }

// Use nullable reference types
public Device? GetDevice(string id) { }

// Use pattern matching
if (device is { IsOnline: true, IpAddress: not null }) { }
```

### MVVM Pattern

```csharp
public partial class MyViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _name;
    
    [RelayCommand]
    private async Task SaveAsync() { }
}
```

### Error Handling

```csharp
try
{
    await DoSomethingAsync();
}
catch (Exception ex)
{
    _log.Error($"[Service] Operation failed: {ex.Message}");
    // Handle or rethrow
}
```

---

## Areas Needing Help

### High Priority

1. **Linux Audio Bridge**
   - Capture system audio via PulseAudio/PipeWire
   - Route to `EchoLink_Sink` virtual device
   - Currently requires manual setup

2. **macOS Support**
   - Audio capture/playback
   - Remote control implementation
   - System commands

3. **iOS Client**
   - Entirely new platform
   - Swift/UIKit or MAUI
   - Requires Apple developer account for testing

4. **Documentation**
   - More examples
   - Video tutorials
   - Troubleshooting guides

5. **Unit Tests**
   - Service layer tests
   - ViewModel tests
   - Integration tests

### Medium Priority

- Hotkey system implementation
- Mesh topology visualization
- Automated VB-Audio Cable installer
- LAN-only fallback mode

---

## Reporting Issues

### Bug Reports

Include:
- OS and version
- .NET version (`dotnet --version`)
- Steps to reproduce
- Expected vs actual behavior
- Console logs (if applicable)
- Screenshots (if UI issue)

### Feature Requests

Include:
- What problem it solves
- How it should work
- Use case examples
- Platform considerations

---

## Questions?

- **General questions:** Use [Discussions](https://github.com/uganthan2005/EchoLink/discussions)
- **Bug reports:** Open an [Issue](https://github.com/uganthan2005/EchoLink/issues)
- **Chat:** Check if there's a Discord/Telegram link in Discussions

---

## Thank You!

Every contribution helps, no matter how small. Whether it's fixing a typo, reporting a bug, or adding a major feature—you're making EchoLink better for everyone.

<div align="center">

**Happy coding! 🚀**

</div>
