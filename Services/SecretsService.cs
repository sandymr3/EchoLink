using System;
using System.IO;
using System.Text.Json;

namespace EchoLink.Services;

public class SecretsService
{
    private static readonly Lazy<SecretsService> _instance = new(() => new SecretsService());
    public static SecretsService Instance => _instance.Value;

    private readonly SecretsData _secrets;

    private SecretsService()
    {
        _secrets = LoadSecrets();
    }

    public string GoogleClientId => _secrets.GoogleClientId ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_ID") ?? "";
    public string GoogleClientSecret => _secrets.GoogleClientSecret ?? Environment.GetEnvironmentVariable("GOOGLE_CLIENT_SECRET") ?? "";

    private SecretsData LoadSecrets()
    {
        try
        {
            // Look for secrets.json in the application's base directory
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string secretsPath = Path.Combine(baseDir, "secrets.json");

            // Also check project root for development
            if (!File.Exists(secretsPath))
            {
                // Navigate up from bin/Debug/net10.0 to find it in the project root if needed
                string? current = baseDir;
                while (current != null)
                {
                    string check = Path.Combine(current, "secrets.json");
                    if (File.Exists(check))
                    {
                        secretsPath = check;
                        break;
                    }
                    current = Directory.GetParent(current)?.FullName;
                }
            }

            if (File.Exists(secretsPath))
            {
                var json = File.ReadAllText(secretsPath);
                return JsonSerializer.Deserialize<SecretsData>(json) ?? new SecretsData();
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"Failed to load secrets: {ex.Message}");
        }
        return new SecretsData();
    }

    private class SecretsData
    {
        public string? GoogleClientId { get; set; }
        public string? GoogleClientSecret { get; set; }
    }
}
