using System;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace EchoLink.Services;

public class SystemControlService
{
    private static SystemControlService? _instance;
    public static SystemControlService Instance => _instance ??= new SystemControlService();

    public Task HandleSystemActionAsync(byte[] payload, CancellationToken ct)
    {
        if (payload.Length >= 1)
        {
            byte actionId = payload[0];  // 0=Lock, 1=Restart, 2=Shutdown
            
            if (actionId == 0 && RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                LockWorkStation();
            }
            // Linux actions would use systemctl/loginctl
        }
        return Task.CompletedTask;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool LockWorkStation();
}