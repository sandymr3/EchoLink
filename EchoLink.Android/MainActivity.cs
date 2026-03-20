using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;
using Avalonia;
using Avalonia.Android;
using System.Threading.Tasks;

namespace EchoLink.Android;

[Activity(
    Label = "EchoLink",
    Theme = "@style/MyTheme.NoActionBar",
    MainLauncher = true,
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode,
    LaunchMode = LaunchMode.SingleTask)]
[IntentFilter(
    new[] { Intent.ActionView },
    Categories = new[] { Intent.CategoryDefault, Intent.CategoryBrowsable },
    DataScheme = "https",
    DataHost = "echo-link.app",
    DataPath = "/oidc/callback",
    AutoVerify = true)]
#pragma warning disable CA1416
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        global::Android.Util.Log.Info("EchoLink", "MainActivity OnCreate");

        // Register the native bridge implementation
        EchoLink.Services.TailscaleService.Instance.NativeBridge = new AndroidNativeMeshBridge();
        EchoLink.Services.AudioStreamingService.Instance.RuntimeBridge = new AndroidAudioRuntimeBridge();

        // Register the Android Browser globally so the Shared project can access it
        EchoLink.App.AndroidBrowserInstance = new EchoLink.Android.Auth.AndroidBrowser();

        // Start the mesh service immediately (don't wait for permission)
        StartMeshService();

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
        if (requestCode == 1 && grantResults.Length > 0 && grantResults[0] == Permission.Granted)
        {
            StartMeshService();
        }
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

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
