using System;
using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;

namespace EchoLink.Services.Auth;

public class MiddlewareClient
{
    public static MiddlewareClient Instance { get; } = new();

    private readonly HttpClient _httpClient;
    private const string MiddlewareBaseUrl = "http://localhost:8081"; // TODO: Load from configuration or environment

    private MiddlewareClient()
    {
        _httpClient = new HttpClient { BaseAddress = new Uri(MiddlewareBaseUrl) };
    }

    private class LoginRequest 
    { 
        [JsonPropertyName("id_token")] 
        public string IdToken { get; set; } = ""; 
    }
    
    private class LoginResponse 
    { 
        [JsonPropertyName("auth_key")] 
        public string AuthKey { get; set; } = ""; 
        
        [JsonPropertyName("username")] 
        public string Username { get; set; } = ""; 
    }

    private class InviteResponse
    {
        [JsonPropertyName("pin")]
        public string Pin { get; set; } = "";
        
        [JsonPropertyName("expires_in_minutes")]
        public int ExpiresInMinutes { get; set; }
    }

    private class ClaimRequest
    {
        [JsonPropertyName("pin")]
        public string Pin { get; set; } = "";
    }

    public async Task<string?> ExchangeJwtForPreAuthKeyAsync(string jwt)
    {
        try
        {
            var request = new LoginRequest { IdToken = jwt };
            var response = await _httpClient.PostAsJsonAsync("/auth/login", request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                LoggingService.Instance.Error($"[Middleware] Failed to exchange JWT. Status: {response.StatusCode}, Body: {errorBody}");
                return null;
            }

            var data = await response.Content.ReadFromJsonAsync<LoginResponse>();
            return data?.AuthKey;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[Middleware] Exception exchanging JWT: {ex.Message}");
            return null;
        }
    }

    public async Task<(string? Pin, int ExpiresInMinutes)> GenerateGuestPinAsync()
    {
        try
        {
            var response = await _httpClient.PostAsync("/auth/guest/invite", null);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                LoggingService.Instance.Error($"[Middleware] Failed to generate guest PIN. Status: {response.StatusCode}, Body: {errorBody}");
                return (null, 0);
            }

            var data = await response.Content.ReadFromJsonAsync<InviteResponse>();
            return (data?.Pin, data?.ExpiresInMinutes ?? 0);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[Middleware] Exception generating guest PIN: {ex.Message}");
            return (null, 0);
        }
    }

    public async Task<string?> ClaimGuestPinAsync(string pin)
    {
        try
        {
            var request = new ClaimRequest { Pin = pin };
            var response = await _httpClient.PostAsJsonAsync("/auth/guest/claim", request);
            
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync();
                LoggingService.Instance.Error($"[Middleware] Failed to claim guest PIN. Status: {response.StatusCode}, Body: {errorBody}");
                return null;
            }

            var data = await response.Content.ReadFromJsonAsync<LoginResponse>();
            return data?.AuthKey;
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[Middleware] Exception claiming guest PIN: {ex.Message}");
            return null;
        }
    }
}
