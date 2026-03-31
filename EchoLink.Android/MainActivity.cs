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
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        global::Android.Util.Log.Info("EchoLink", "MainActivity OnCreate");

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
            global::Android.Util.Log.Info("EchoLink", $"Deep Link intercepted: {intent.DataString}");
            
            // Manually parse and trigger the login logic if it looks like our deep link
            // This acts as a fallback if Avalonia's Activated event doesn't fire for OnNewIntent
            var uri = new System.Uri(intent.DataString);
            if (uri.Scheme == "echolink" && uri.Host == "login")
            {
                 global::Android.Util.Log.Info("EchoLink", "Manual deep link handling triggered.");
                 var query = System.Web.HttpUtility.ParseQueryString(uri.Query);
                 var authKey = query["authkey"];
                 
                 if (!string.IsNullOrEmpty(authKey))
                 {
                     // We need to run this on the UI thread or ensure thread safety
                     Avalonia.Threading.Dispatcher.UIThread.Post(async () => {
                         // Find the App instance and call a public method to finish login
                         if (Avalonia.Application.Current is App app)
                         {
                             // We can expose PerformDeepLinkLogin as internal/public or use reflection, 
                             // but for now let's just use the existing plumbing if possible.
                             // Since 'PerformDeepLinkLogin' is private in App.axaml.cs, we might need to expose it.
                             
                             // Actually, let's just create a helper method in App.axaml.cs
                             await app.HandleDeepLink(uri);
                         }
                     });
                 }
            }
        }
    }

    protected override void OnResume()
    {
        base.OnResume();
    }

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        // Register dependencies early so they are available during App initialization
        EchoLink.App.AndroidActivityInstance = this;
        EchoLink.Services.TailscaleService.Instance.NativeBridge = new AndroidNativeMeshBridge();
        EchoLink.Services.AudioStreamingService.Instance.RuntimeBridge = new AndroidAudioRuntimeBridge();
        EchoLink.Services.AppShieldService.Instance = new EchoLink.Services.AndroidAppShieldService();
        EchoLink.Services.Auth.AuthService.PlatformHandler = new EchoLink.Android.Auth.AndroidAuthPlatformHandler();

        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
