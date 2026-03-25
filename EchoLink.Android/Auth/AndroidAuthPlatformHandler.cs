using System;
using System.Threading.Tasks;
using EchoLink.Services;
using EchoLink.Services.Auth;
using Android.Content;
using Android.App;

namespace EchoLink.Android.Auth;

public class AndroidAuthPlatformHandler : IAuthPlatformHandler
{
    public Task<string?> LoginAsync()
    {
        try 
        {
            var uri = "https://api.echo-link.app/auth/login?device=android";
            var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(uri));
            intent.AddFlags(ActivityFlags.NewTask);
            Application.Context.StartActivity(intent);
            return Task.FromResult<string?>(null);
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[AuthService] Android Browser Launch Failed: {ex.Message}");
            return Task.FromResult<string?>(null);
        }
    }
}
