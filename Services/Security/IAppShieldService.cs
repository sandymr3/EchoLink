using System.Threading.Tasks;

namespace EchoLink.Services;

public interface IAppShieldService
{
    Task<bool> IsShieldConfiguredAsync();
    Task<bool> PromptUnlockAsync(string reason);
    Task SetupShieldAsync();
    Task SetupLinuxPinAsync(string pin);
}
