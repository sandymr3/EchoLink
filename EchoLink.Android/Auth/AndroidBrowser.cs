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

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        TaskCompletionSource = new TaskCompletionSource<string>();

        // Launch the StartUrl using an Android Intent (opens Default Browser or Chrome Custom Tab)
        var intent = new Intent(Intent.ActionView, global::Android.Net.Uri.Parse(options.StartUrl));
        intent.AddFlags(ActivityFlags.NewTask);
        
        var context = Application.Context;
        if (context != null)
        {
            context.StartActivity(intent);
        }
        else
        {
            return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = "Application context is null" };
        }

        // Wait for MainActivity.OnNewIntent to resolve the TaskCompletionSource
        try 
        {
            string result = await TaskCompletionSource.Task.WaitAsync(cancellationToken);
            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = result
            };
        }
        catch (TaskCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Login timed out or was cancelled." };
        }
        catch (Exception ex)
        {
             return new BrowserResult { ResultType = BrowserResultType.UnknownError, Error = ex.Message };
        }
    }
}
