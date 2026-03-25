using System.Threading.Tasks;

namespace EchoLink.Services.Auth;

public interface IAuthPlatformHandler
{
    Task<string?> LoginAsync();
}
