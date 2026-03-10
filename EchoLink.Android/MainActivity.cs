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
    ConfigurationChanges = ConfigChanges.Orientation | ConfigChanges.ScreenSize | ConfigChanges.UiMode)]
#pragma warning disable CA1416
public class MainActivity : AvaloniaMainActivity<App>
{
    protected override void OnCreate(Bundle? savedInstanceState)
    {
        base.OnCreate(savedInstanceState);
        global::Android.Util.Log.Info("EchoLink", "MainActivity OnCreate");

        // Register the native bridge implementation
        EchoLink.Services.TailscaleService.Instance.NativeBridge = new AndroidNativeMeshBridge();

        // Start the mesh service immediately (don't wait for permission)
        StartMeshService();

        // Request notification permission for Android 13+ (for the persistent notification)
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            if (CheckSelfPermission(global::Android.Manifest.Permission.PostNotifications) != Permission.Granted)
            {
                global::Android.Util.Log.Info("EchoLink", "Requesting PostNotifications permission...");
                RequestPermissions(new string[] { global::Android.Manifest.Permission.PostNotifications }, 1);
            }
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

    protected override AppBuilder CustomizeAppBuilder(AppBuilder builder)
    {
        return base.CustomizeAppBuilder(builder)
            .WithInterFont();
    }
}
