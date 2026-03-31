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

public sealed class LinuxAppShieldService : IAppShieldService
{
    private const int SaltSizeBytes = 16;
    private const int HashSizeBytes = 32;
    private const int Iterations = 120000;

    public Task<bool> IsShieldConfiguredAsync()
    {
        var settings = SettingsService.Instance.Load();
        bool configured =
            !string.IsNullOrWhiteSpace(settings.LinuxAppShieldPinSalt) &&
            !string.IsNullOrWhiteSpace(settings.LinuxAppShieldPinHash);

        return Task.FromResult(configured);
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
        settings.LinuxAppShieldPinSalt = Convert.ToBase64String(salt);
        settings.LinuxAppShieldPinHash = Convert.ToBase64String(hash);
        settings.LinuxAppShieldPinIterations = Iterations;
        SettingsService.Instance.Save(settings);

        return Task.CompletedTask;
    }

    public async Task<bool> PromptUnlockAsync(string reason)
    {
        var settings = SettingsService.Instance.Load();
        if (string.IsNullOrWhiteSpace(settings.LinuxAppShieldPinSalt) ||
            string.IsNullOrWhiteSpace(settings.LinuxAppShieldPinHash))
        {
            return false;
        }

        var pin = await PromptForPinAsync("Unlock EchoLink", string.IsNullOrWhiteSpace(reason) ? "Enter your 4-digit PIN" : reason);
        if (!IsValidPin(pin))
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(settings.LinuxAppShieldPinSalt);
            byte[] expectedHash = Convert.FromBase64String(settings.LinuxAppShieldPinHash);
            int iterations = settings.LinuxAppShieldPinIterations > 0 ? settings.LinuxAppShieldPinIterations : Iterations;

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
