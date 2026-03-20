using System;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient;
using Duende.IdentityModel.OidcClient.Browser;

namespace EchoLink.Services.Auth;

public class AuthService
{
    private readonly OidcClient _oidcClient;

    public AuthService(IBrowser browser, string redirectUri)
    {
        var options = new OidcClientOptions
        {
            Authority = "https://accounts.google.com",
            ClientId = "your_google_client_id_here.apps.googleusercontent.com", // TODO: Extract to configuration
            RedirectUri = redirectUri,
            Scope = "openid email",
            FilterClaims = false,
            Browser = browser
        };

        _oidcClient = new OidcClient(options);
    }

    public async Task<string?> LoginAsync()
    {
        try
        {
            var result = await _oidcClient.LoginAsync(new LoginRequest());
            
            if (result.IsError)
            {
                LoggingService.Instance.Error($"[AuthService] OIDC Login Error: {result.Error}");
                return null;
            }

            // Return the IdentityToken (JWT) which contains the user's email securely signed by Google
            return result.IdentityToken;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[AuthService] Exception during login: {ex.Message}");
            return null;
        }
    }
}
