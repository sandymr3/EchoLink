using System.Threading.Tasks;

namespace EchoLink.Services;

public static class AppShieldService
{
    public static IAppShieldService Instance { get; set; } = new NoOpAppShieldService();
}

internal sealed class NoOpAppShieldService : IAppShieldService
{
    public Task<bool> IsShieldConfiguredAsync()
    {
        return Task.FromResult(false);
    }

    public Task<bool> PromptUnlockAsync(string reason)
    {
        return Task.FromResult(true);
    }

    public Task SetupShieldAsync()
    {
        return Task.CompletedTask;
    }

    public Task SetupLinuxPinAsync(string pin)
    {
        return Task.CompletedTask;
    }
}
