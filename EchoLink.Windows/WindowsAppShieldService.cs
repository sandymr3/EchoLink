using System;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using EchoLink.Views;

namespace EchoLink.Services;

public sealed class WindowsAppShieldService : IAppShieldService
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 120000;

    public async Task<bool> IsShieldConfiguredAsync()
    {
        await Task.CompletedTask;

        var settings = SettingsService.Instance.Load();
        return !string.IsNullOrWhiteSpace(settings.WindowsAppShieldPinSalt) &&
               !string.IsNullOrWhiteSpace(settings.WindowsAppShieldPinHash);
    }

    public async Task<bool> PromptUnlockAsync(string reason)
    {
        var settings = SettingsService.Instance.Load();
        if (string.IsNullOrWhiteSpace(settings.WindowsAppShieldPinSalt) ||
            string.IsNullOrWhiteSpace(settings.WindowsAppShieldPinHash))
        {
            return false;
        }

        var pin = await PromptForPinAsync(
            "Unlock EchoLink",
            string.IsNullOrWhiteSpace(reason) ? "Enter your 4-digit PIN" : reason);

        if (!IsValidPin(pin))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(settings.WindowsAppShieldPinSalt);
            byte[] expectedHash = Convert.FromBase64String(settings.WindowsAppShieldPinHash);
            int iterations = settings.WindowsAppShieldPinIterations > 0
                ? settings.WindowsAppShieldPinIterations
                : Iterations;

            byte[] actualHash = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(pin!),
                salt,
                iterations,
                HashAlgorithmName.SHA256,
                expectedHash.Length);

            return CryptographicOperations.FixedTimeEquals(expectedHash, actualHash);
        }
        catch
        {
            return false;
        }
    }

    public async Task SetupShieldAsync()
    {
        var first = await PromptForPinAsync("Set Up App Shield", "Create a 4-digit PIN");
        if (!IsValidPin(first))
        {
            return;
        }

        var confirm = await PromptForPinAsync("Confirm PIN", "Re-enter your 4-digit PIN");
        if (!IsValidPin(confirm) || !string.Equals(first, confirm, StringComparison.Ordinal))
        {
            return;
        }

        await SetupLinuxPinAsync(first!);
    }

    public Task SetupLinuxPinAsync(string pin)
    {
        if (!IsValidPin(pin))
        {
            return Task.CompletedTask;
        }

        Span<byte> salt = stackalloc byte[SaltSizeBytes];
        RandomNumberGenerator.Fill(salt);

        byte[] hash = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(pin),
            salt,
            Iterations,
            HashAlgorithmName.SHA256,
            HashSizeBytes);

        var settings = SettingsService.Instance.Load();
        settings.WindowsAppShieldPinSalt = Convert.ToBase64String(salt);
        settings.WindowsAppShieldPinHash = Convert.ToBase64String(hash);
        settings.WindowsAppShieldPinIterations = Iterations;
        SettingsService.Instance.Save(settings);

        return Task.CompletedTask;
    }

    private static bool IsValidPin(string? pin)
    {
        if (string.IsNullOrWhiteSpace(pin) || pin.Length != 4)
        {
            return false;
        }

        foreach (char c in pin)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static async Task<string?> PromptForPinAsync(string title, string subtitle)
    {
        var tcs = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

        Dispatcher.UIThread.Post(async () =>
        {
            try
            {
                var owner = GetOwnerWindow();
                if (owner == null)
                {
                    tcs.TrySetResult(null);
                    return;
                }

                var pinPad = new PinPadView(title, subtitle);
                string? pin = await pinPad.ShowDialog<string?>(owner);
                tcs.TrySetResult(pin);
            }
            catch
            {
                tcs.TrySetResult(null);
            }
        });

        return await tcs.Task.ConfigureAwait(false);
    }

    private static Window? GetOwnerWindow()
    {
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            return desktop.MainWindow;
        }

        return null;
    }
}
