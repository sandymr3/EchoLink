using Android.App;
using Android.Content;
using Android.OS;
using Android.Runtime;
using AndroidX.Core.App;
using System;
using System.IO;
using System.Threading.Tasks;
using System.Runtime.Versioning;

namespace EchoLink.Android;

[Service(Name = "com.echolink.app.EchoLinkForegroundService", 
         ForegroundServiceType = global::Android.Content.PM.ForegroundService.TypeDataSync | global::Android.Content.PM.ForegroundService.TypeSpecialUse,
         Exported = false)]
#pragma warning disable CA1416
public class EchoLinkForegroundService : Service
{
    private const int NOTIFICATION_ID = 1001;
    private const string CHANNEL_ID = "EchoLink_Mesh_Channel";

    public override IBinder? OnBind(Intent? intent) => null;

    [return: GeneratedEnum]
    public override StartCommandResult OnStartCommand(Intent? intent, [GeneratedEnum] StartCommandFlags flags, int startId)
    {
        string action = intent?.Action ?? "";
        global::Android.Util.Log.Info("EchoLinkService", $"OnStartCommand action: {action}");

        if (action == "START_SERVICE")
        {
            StartForegroundService();
            
            // Launch the node in a background thread so we don't block the UI
            Task.Run(() =>
            {
                try
                {
                    var filesDir = GetExternalFilesDir(null);
                    if (filesDir == null) {
                        global::Android.Util.Log.Error("EchoLinkService", "GetExternalFilesDir(null) is NULL!");
                        return;
                    }

                    string configDir = Path.Combine(filesDir.AbsolutePath, "tailscale");
                    if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                    // Quick helper to get the Android device's local Wi-Fi IP
                    string localIp = "";
                    try
                    {
                        foreach (var netInterface in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ||
                                netInterface.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ||
                                netInterface.Name.Contains("wlan", StringComparison.OrdinalIgnoreCase))
                            {
                                foreach (var addrInfo in netInterface.GetIPProperties().UnicastAddresses)
                                {
                                    if (addrInfo.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork && !System.Net.IPAddress.IsLoopback(addrInfo.Address))
                                    {
                                        localIp = addrInfo.Address.ToString();
                                    }
                                }
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        global::Android.Util.Log.Warn("EchoLinkService", $"Failed to fetch local IP: {ex.Message}");
                    }

                    global::Android.Util.Log.Info("EchoLinkService", $"Calling StartEchoLinkNode with dir: {configDir}, Local IP: {localIp}");
                    int result = NativeMethods.StartEchoLinkNode(configDir, "", global::Android.OS.Build.Model ?? "Android", localIp);
                    global::Android.Util.Log.Info("EchoLinkService", $"StartEchoLinkNode returned: {result}");
                }
                catch (Exception ex)
                {
                    global::Android.Util.Log.Error("EchoLinkService", $"Exception starting node: {ex.Message}");
                }
            });
        }
        else if (action == "STOP_SERVICE")
        {
            NativeMethods.StopEchoLinkNode();
            StopServiceForeground();
            StopSelf();
        }

        return StartCommandResult.Sticky;
    }

    private void StopServiceForeground()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.Tiramisu)
        {
            StopForeground(StopForegroundFlags.Remove);
        }
        else
        {
            #pragma warning disable CA1422
            StopForeground(true);
            #pragma warning restore CA1422
        }
    }

    private void StartForegroundService()
    {
        CreateNotificationChannel();

        var notification = new NotificationCompat.Builder(this, CHANNEL_ID)
            .SetContentTitle("EchoLink Mesh Active")
            .SetContentText("Tailscale & SSH are running in the background.")
            // Use built-in system icon as fallback
            .SetSmallIcon(global::Android.Resource.Drawable.StatNotifySync)
            .SetOngoing(true)
            .SetPriority(NotificationCompat.PriorityLow)
            .Build();

        StartForeground(NOTIFICATION_ID, notification);
    }

    private void CreateNotificationChannel()
    {
        if (Build.VERSION.SdkInt >= BuildVersionCodes.O)
        {
            var channel = new NotificationChannel(CHANNEL_ID, "EchoLink Service", NotificationImportance.Low)
            {
                Description = "Foreground service for EchoLink Mesh networking"
            };
            var manager = (NotificationManager)GetSystemService(NotificationService)!;
            manager.CreateNotificationChannel(channel);
        }
    }

    public override void OnDestroy()
    {
        NativeMethods.StopEchoLinkNode();
        base.OnDestroy();
    }
}
