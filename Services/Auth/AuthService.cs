using System;
using System.Threading.Tasks;

namespace EchoLink.Services.Auth;

public class AuthService
{
    public static IAuthPlatformHandler? PlatformHandler { get; set; }

    public async Task<string?> LoginAsync()
    {
        if (PlatformHandler != null)
        {
            return await PlatformHandler.LoginAsync();
        }
        
        // Default to PC handler if not set
        return await new DefaultAuthPlatformHandler().LoginAsync();
    }
}
