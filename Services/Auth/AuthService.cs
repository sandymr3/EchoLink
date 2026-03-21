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
            ClientId = SecretsService.Instance.GoogleClientId,
            ClientSecret = SecretsService.Instance.GoogleClientSecret,
            RedirectUri = redirectUri,
            Scope = "openid email",
            FilterClaims = false,
            Browser = browser
        };

        // THE C# DYNAMIC BYPASS:
        // This modifies the default policy at runtime using reflection.
        // It completely bypasses the CS0400 missing assembly compiler error!
        dynamic discoveryPolicy = options.Policy.Discovery;
        discoveryPolicy.ValidateEndpoints = false;
        discoveryPolicy.ValidateIssuerName = false;

        _oidcClient = new OidcClient(options);
    }

    public async Task<string?> LoginAsync()
    {
        try
        {
            LoggingService.Instance.Info("[AuthService] Initiating OIDC Login request...");
            var result = await _oidcClient.LoginAsync(new LoginRequest());
            
            if (result.IsError)
            {
                string errorMsg = $"OIDC Login Error: {result.Error} | Description: {result.ErrorDescription}";
                LoggingService.Instance.Error($"[AuthService] {errorMsg}");
                throw new Exception(errorMsg);
            }

            if (string.IsNullOrEmpty(result.IdentityToken))
            {
                string errorMsg = $"OIDC Login Success, but IdentityToken is NULL. AccessToken length: {result.AccessToken?.Length}";
                LoggingService.Instance.Error($"[AuthService] {errorMsg}");
                throw new Exception(errorMsg);
            }

            // Return the IdentityToken (JWT) which contains the user's email securely signed by Google
            return result.IdentityToken;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[AuthService] Exception during login: {ex.Message}");
            throw; // Rethrow to let ViewModel display it in StatusText
        }
    }
}
