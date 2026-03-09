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
        Console.WriteLine($"[AndroidService] OnStartCommand received. Action: {action}");

        if (action == "START_SERVICE")
        {
            StartForegroundService();
            
            // Launch the node in a background thread so we don't block the UI
            Task.Run(() =>
            {
                try
                {
                    string configDir = Path.Combine(GetExternalFilesDir(null)!.AbsolutePath, "tailscale");
                    if (!Directory.Exists(configDir)) Directory.CreateDirectory(configDir);

                    Console.WriteLine($"[AndroidService] Calling NativeMethods.StartEchoLinkNode with dir: {configDir}");
                    // For now, using empty authKey for interactive login
                    int result = NativeMethods.StartEchoLinkNode(configDir, "", Build.Model ?? "Android Device");
                    Console.WriteLine($"[AndroidService] StartEchoLinkNode result: {result}");
                }
                catch (Exception ex)
                {
                    // Log error to console/log
                    Console.WriteLine($"[AndroidService] Error starting node: {ex.Message}");
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
