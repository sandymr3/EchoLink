using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using System.Threading.Tasks;

namespace EchoLink.Android;

[Activity(
    Name = "com.echolink.app.MainActivity",
    Label = "EchoLink",
    Theme = "@style/MyTheme.NoActionBar",
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    LaunchMode = LaunchMode.SingleTask)]
#pragma warning disable CA1416
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        global::Android.Util.Log.Info("EchoLink", "MainActivity OnCreate");

        // Register the activity instance
        EchoLink.App.AndroidActivityInstance = this;

        // Register the native bridge implementation
        EchoLink.Services.TailscaleService.Instance.NativeBridge = new AndroidNativeMeshBridge();
        EchoLink.Services.AudioStreamingService.Instance.RuntimeBridge = new AndroidAudioRuntimeBridge();

        // Register the Android Browser globally so the Shared project can access it
        EchoLink.App.AndroidBrowserInstance = new EchoLink.Android.Auth.AndroidBrowser();

        // REMOVED: Premature StartMeshService call. 
        // We will only start the service when we have an AuthKey during login.

        // Request notification and storage permissions for Android
        var permissions = new System.Collections.Generic.List<string>();

        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                permissions.Add(global::Android.Manifest.Permission.PostNotifications);
            }
        }

        // Request storage permission to write to Downloads
        if (CheckSelfPermission(global::Android.Manifest.Permission.WriteExternalStorage) != Permission.Granted ||
            CheckSelfPermission(global::Android.Manifest.Permission.ReadExternalStorage) != Permission.Granted)
        {
            permissions.Add(global::Android.Manifest.Permission.WriteExternalStorage);
            permissions.Add(global::Android.Manifest.Permission.ReadExternalStorage);
        }

        if (CheckSelfPermission(global::Android.Manifest.Permission.RecordAudio) != Permission.Granted)
        {
            permissions.Add(global::Android.Manifest.Permission.RecordAudio);
        }

        if (permissions.Count > 0)
        {
            global::Android.Util.Log.Info("EchoLink", "Requesting permissions...");
            RequestPermissions(permissions.ToArray(), 1);
        }
    }

    public override void OnRequestPermissionsResult(int requestCode, string[] permissions, Permission[] grantResults)
    {
        base.OnRequestPermissionsResult(requestCode, permissions, grantResults);
        // Removed StartMeshService() call here to prevent premature node startup.
    }

    private void StartMeshService()
    {
        var intent = new Intent(this, typeof(EchoLinkForegroundService));
        intent.SetAction("START_SERVICE");
        
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            StartForegroundService(intent);
        }
        else
        {
            StartService(intent);
        }
    }

    // Intercept the Deep Link callback from the Browser
    protected override void OnNewIntent(Intent? intent)
    {
        base.OnNewIntent(intent);

        if (intent?.Action == Intent.ActionView && intent.DataString != null)
        {
            if (intent.DataString.StartsWith("https://echo-link.app/oidc/callback"))
            {
                global::Android.Util.Log.Info("EchoLink", $"OIDC Callback intercepted: {intent.DataString}");
                // Unblock the login flow by passing the callback URL
                EchoLink.Android.Auth.AndroidBrowser.TaskCompletionSource?.TrySetResult(intent.DataString);
            }
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
        
        // If the user manually navigated back to the app without an intent (e.g. they backed out of the browser),
        // we should eventually timeout the task so the UI doesn't hang forever.
        // We'll give them a short grace period in case the intent is still processing.
        _ = Task.Run(async () =>
        {
            await Task.Delay(1500); // Wait 1.5 seconds to see if OnNewIntent fires
            if (EchoLink.Android.Auth.AndroidBrowser.TaskCompletionSource != null && 
                !EchoLink.Android.Auth.AndroidBrowser.TaskCompletionSource.Task.IsCompleted)
            {
                global::Android.Util.Log.Info("EchoLink", "User returned to app without OIDC intent. Cancelling login flow.");
                EchoLink.Android.Auth.AndroidBrowser.TaskCompletionSource.TrySetCanceled();
            }
        });
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
