using System;
using System.IO;
using System.Reflection;
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
            // 1. Try to load from Embedded Resource (Best for Android/Production)
            var assembly = Assembly.GetExecutingAssembly();
            // Resource name is usually [DefaultNamespace].[FileName]
            var resourceName = "EchoLink.secrets.json";

            using (Stream? stream = assembly.GetManifestResourceStream(resourceName))
            {
                if (stream != null)
                {
                    using (StreamReader reader = new StreamReader(stream))
                    {
                        string json = reader.ReadToEnd();
                        var data = JsonSerializer.Deserialize<SecretsData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
                        if (data != null) return data;
                    }
                }
            }

            // 2. Fallback to local file system (Best for Desktop Development)
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string secretsPath = Path.Combine(baseDir, "secrets.json");

            if (!File.Exists(secretsPath))
            {
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
                return JsonSerializer.Deserialize<SecretsData>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new SecretsData();
            }
        }
        catch (Exception ex)
        {
            // LoggingService may not be ready yet, use Console as fallback
            Console.WriteLine($"[SecretsService] Critical failure loading secrets: {ex.Message}");
        }
        return new SecretsData();
    }

    private class SecretsData
    {
        public string? GoogleClientId { get; set; }
        public string? GoogleClientSecret { get; set; }
    }
}
