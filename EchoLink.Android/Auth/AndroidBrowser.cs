using System;
using System.Threading;
using System.Threading.Tasks;
using Android.Content;
using Android.Net;
using Application = Android.App.Application;
using Duende.IdentityModel.OidcClient.Browser;

namespace EchoLink.Android.Auth;

public class AndroidBrowser : IBrowser
{
    // Holds the state to unblock the login flow when the callback intent is intercepted
    public static TaskCompletionSource<string>? TaskCompletionSource;

    public Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource = new TaskCompletionSource<string>();

        // Launch the StartUrl using an Android Intent (opens Default Browser or Chrome Custom Tab)
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(options.StartUrl));
        intent.AddFlags(ActivityFlags.NewTask);
        
        if (Application.Context != null)
        {
            Application.Context.StartActivity(intent);
        }

        // Wait for MainActivity.OnNewIntent to resolve the TaskCompletionSource
        return TaskCompletionSource.Task.ContinueWith(t => new BrowserResult
        {
            ResultType = BrowserResultType.Success,
            Response = t.Result
        }, cancellationToken);
    }
}
