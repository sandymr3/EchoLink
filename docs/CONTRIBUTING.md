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
2. Check existing tests for patterns
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

# Run tests
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

**Commit message format (Conventional Commits):**
```
feat: add macro sync support
fix: android path resolution for SFTP
docs: update SETUP.md with Headscale guide
refactor: extract clipboard journal logic
test: add unit tests for AuthService
chore: update Avalonia to 11.3.12
```

**Types:**
- `feat:` - New feature
- `fix:` - Bug fix
- `docs:` - Documentation only
- `refactor:` - Code reorganization (not a feature or fix)
- `test:` - Adding/updating tests
- `chore:` - Build, config, maintenance

### 7. Push and Create PR

```bash
git push origin feature/your-feature-name
```

Then on GitHub:
1. Click "Compare & pull request"
2. Fill out the template
3. Link related issues: `Closes #123`
4. Submit

**PR template:**
```markdown
## Description
What does this PR do?

## Related Issue
Fixes #123

## Testing
How did you test this?

## Screenshots (if UI changes)
Before/after screenshots

## Checklist
- [ ] Code builds without errors
- [ ] Tests pass
- [ ] Manual testing completed
- [ ] Documentation updated (if needed)
```

### 8. Code Review

A maintainer will review your PR. They might:
- Request changes
- Ask questions
- Suggest improvements

**Respond promptly** and make requested changes. Once approved, your PR will be merged.

---

## Reporting Issues

### Bug Reports

**Before reporting:**
- Search existing issues
- Try latest version
- Check documentation

**Include:**
```markdown
**Describe the bug**
Clear description of what's wrong

**To Reproduce**
Steps to reproduce:
1. Go to '...'
2. Click '...'
3. See error

**Expected behavior**
What should happen?

**Screenshots**
If applicable

**Environment:**
- OS: Windows 11 / Ubuntu 22.04 / Android 13
- .NET version: 10.0.x
- EchoLink version: v1.0

**Logs**
Console output, error messages
```

### Feature Requests

```markdown
**Is your feature request related to a problem?**
"I'm always frustrated when..."

**Describe the solution you'd like**
Clear description

**Describe alternatives you've considered**
Other solutions you thought about

**Additional context**
Mockups, examples, use cases
```

---

## Coding Standards

### C# Conventions

**Naming:**
```csharp
public class DeviceViewModel { }           // Classes: PascalCase
public interface IAuthService { }          // Interfaces: IPascalCase
public void DoSomethingAsync() { }         // Methods: PascalCase + Async suffix
private readonly string _apiKey;           // Fields: _camelCase
```

**Async:**
```csharp
// ✅ Good
public async Task<Device> GetDeviceAsync(string id) { }

// ❌ Bad
public async Task<Device> GetDevice(string id) { }      // Missing Async suffix
public Device GetDevice(string id) { }                  // Async method without Task
public async void DoSomething() { }                     // async void (except events)
```

**Dependency Injection:**
```csharp
public class MyViewModel : ViewModelBase
{
    private readonly IClipboardSyncService _clipboard;
    
    public MyViewModel(IClipboardSyncService clipboard)
    {
        _clipboard = clipboard;
    }
}
```

**Error Handling:**
```csharp
// ✅ Good - specific exceptions
try
{
    await sshClient.ConnectAsync();
}
catch (SshConnectionException ex)
{
    _logger.LogError(ex, "Failed to connect to {Host}", host);
    throw;
}

// ❌ Bad - catch all
try
{
    await DoSomething();
}
catch (Exception)
{
    // Swallowing errors
}
```

### MVVM Pattern

**ViewModels:**
- Inherit from `ViewModelBase`
- Use `[ObservableProperty]` from CommunityToolkit
- No UI logic in ViewModels (use Commands)

```csharp
public partial class DashboardViewModel : ViewModelBase
{
    [ObservableProperty]
    private string _deviceName;
    
    [RelayCommand]
    private async Task ConnectAsync()
    {
        // Command logic
    }
}
```

**Views:**
- XAML only (no code-behind logic)
- Use `{Binding}` for data
- Use `Commands` for actions

---

## Documentation

### Updating Docs

If your PR:
- Adds a feature → Update SETUP.md
- Changes architecture → Update DEVELOPER.md
- Fixes a gotcha → Add to DEVELOPER.md "Common Pitfalls"

**Documentation style:**
- Clear and concise
- Include code examples
- Mention platform differences
- Add troubleshooting tips

---

## First-Time Contributors

**Good places to start:**

1. **Documentation** - Fix typos, clarify steps, add examples
2. **Tests** - Add unit tests for uncovered Services
3. **Small bugs** - Look for `good first issue` label
4. **Platform testing** - Test on your OS, report issues

**Don't be shy!** Everyone starts somewhere. Ask questions in issues or discussions.

---

## Areas Needing Help

### 🐧 Linux Audio

**Problem:** System audio capture requires manual PulseAudio/PipeWire routing

**What's needed:**
- Auto-configure PulseAudio module
- Create `EchoLink_Sink` on startup
- Document PipeWire equivalent

**Files:** `Services/Audio/AudioStreamingService.cs`, `EchoLink.Linux/`

### 🍎 macOS Support

**Problem:** Limited macOS-specific implementations

**What's needed:**
- Audio capture (CoreAudio)
- Remote control (CGEvent)
- Telemetry (sysctl, IOKit)

**Files:** Create `EchoLink.macOS/` directory

### 📱 iOS Client

**Problem:** Doesn't exist yet

**What's needed:**
- Everything! This is a greenfield implementation
- Would need iOS-specific networking (no userspace Tailscale on iOS)
- SwiftUI or UIKit frontend

**Note:** This is a big undertaking. Discuss approach in Discussions first.

### 🧪 Test Coverage

**Problem:** Not enough unit tests

**What's needed:**
- Tests for all Services
- Mock Tailscale/SSH dependencies
- Integration tests for pairing flow

**Files:** `EchoLink.Tests/` (create if needed)

---

## Questions?

**Not sure where to start?**
- Open a [Discussion](https://github.com/uganthan2005/EchoLink/discussions)
- Comment on an issue asking for context
- Join the project chat (link in repo)

**Found a documentation gap?**
- Open an issue with `[docs]` label
- Submit a PR with improvements

---

## Code of Conduct

- Be respectful and inclusive
- Help newcomers
- Focus on constructive feedback
- No harassment or discrimination

**TL;DR:** Be nice. We're all here to build cool stuff together.

---

## License

By contributing, you agree that your contributions will be licensed under the MIT License (same as the project).

---

**Ready to start?** Pick an issue, fork the repo, and let's build something awesome! 🚀
