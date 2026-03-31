using System;
using System.Threading.Tasks;
using Android.App;
using AndroidX.Biometric;
using AndroidX.Core.Content;
using AndroidX.Fragment.App;

namespace EchoLink.Services;

public sealed class AndroidAppShieldService : IAppShieldService
{
    private const int AllowedAuthenticators =
        (int)(BiometricManager.Authenticators.BiometricStrong | BiometricManager.Authenticators.DeviceCredential);

    public Task SetupShieldAsync()
    {
        return Task.CompletedTask;
    }

    public Task SavePinAsync(string pin)
    {
        return Task.CompletedTask;
    }

    public Task<bool> IsShieldConfiguredAsync()
    {
        try
        {
            var activity = App.AndroidActivityInstance as Activity;
            if (activity == null)
            {
                return Task.FromResult(false);
            }

            var manager = BiometricManager.From(activity);
            var result = manager.CanAuthenticate(AllowedAuthenticators);
            return Task.FromResult(result == BiometricManager.BiometricSuccess);
        }
        catch
        {
            return Task.FromResult(false);
        }
    }

    public async Task<bool> PromptUnlockAsync(string reason)
    {
        var activity = App.AndroidActivityInstance as FragmentActivity;
        if (activity == null)
        {
            return false;
        }

        var manager = BiometricManager.From(activity);
        var canAuth = manager.CanAuthenticate(AllowedAuthenticators);
        if (canAuth != BiometricManager.BiometricSuccess)
        {
            return false;
        }

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var executor = ContextCompat.GetMainExecutor(activity);
        var callback = new UnlockCallback(tcs);
        var prompt = new BiometricPrompt(activity, executor, callback);

        var promptInfo = new BiometricPrompt.PromptInfo.Builder()
            .SetTitle("Unlock EchoLink")
            .SetSubtitle(string.IsNullOrWhiteSpace(reason) ? "Authenticate to continue" : reason)
            .SetAllowedAuthenticators(AllowedAuthenticators)
            .Build();

        activity.RunOnUiThread(() => prompt.Authenticate(promptInfo));

        return await tcs.Task.ConfigureAwait(false);
    }

    private sealed class UnlockCallback : BiometricPrompt.AuthenticationCallback
    {
        private readonly TaskCompletionSource<bool> _tcs;

        public UnlockCallback(TaskCompletionSource<bool> tcs)
        {
            _tcs = tcs;
        }

        public override void OnAuthenticationSucceeded(BiometricPrompt.AuthenticationResult result)
        {
            base.OnAuthenticationSucceeded(result);
            _tcs.TrySetResult(true);
        }

        public override void OnAuthenticationError(int errorCode, Java.Lang.ICharSequence? errString)
        {
            base.OnAuthenticationError(errorCode, errString);
            _tcs.TrySetResult(false);
        }

        public override void OnAuthenticationFailed()
        {
            base.OnAuthenticationFailed();
            // Keep prompt open until the user succeeds, cancels, or an unrecoverable error occurs.
        }
    }
}
