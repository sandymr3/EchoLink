using EchoLink.Services;
using System;
using System.Threading.Tasks;
using Android.Content;
using Android.App;

namespace EchoLink.Android;

public class AndroidNativeMeshBridge : INativeMeshBridge
{
    private bool _libraryLoaded = true;

    public AndroidNativeMeshBridge()
    {
        Console.WriteLine("[NativeBridge-UltraDebug] Initializing AndroidNativeMeshBridge...");
        try 
        {
            // Test if we can call a simple method to verify library load
            Console.WriteLine("[NativeBridge-UltraDebug] Attempting to call NativeMethods.GetBackendState()...");
            var statePtr = NativeMethods.GetBackendState();
            var state = System.Runtime.InteropServices.Marshal.PtrToStringAnsi(statePtr) ?? "Unknown";
            Console.WriteLine($"[NativeBridge-UltraDebug] Call successful! Initial State: {state}");
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] CRITICAL: libecholink.so not found! {ex.Message}");
            _libraryLoaded = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] CRITICAL: Unexpected error loading native library: {ex.GetType().Name} - {ex.Message}");
            _libraryLoaded = false;
        }
    }

    public string GetBackendState()
    {
        if (!_libraryLoaded) return "LibraryLoadError";
        try
        {
            IntPtr ptr = NativeMethods.GetBackendState();
            return ptr == IntPtr.Zero ? "Stopped" : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? "Stopped";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] GetBackendState failed: {ex.Message}");
            return "LibraryLoadError";
        }
    }

    public string? GetTailscaleIp()
    {
        if (!_libraryLoaded) return null;
        try
        {
            IntPtr ptr = NativeMethods.GetTailscaleIp();
            return ptr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] GetTailscaleIp failed: {ex.Message}");
            return null;
        }
    }

    public string? GetLoginUrl()
    {
        if (!_libraryLoaded) return null;
        try
        {
            IntPtr ptr = NativeMethods.GetLoginUrl();
            return ptr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] GetLoginUrl failed: {ex.Message}");
            return null;
        }
    }

    public string GetPeerListJson()
    {
        if (!_libraryLoaded) return "[]";
        try
        {
            IntPtr ptr = NativeMethods.GetPeerListJson();
            if (ptr == IntPtr.Zero) return "[]";
            return System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr) ?? "[]";
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] GetPeerListJson failed: {ex.Message}");
            return "[]";
        }
    }

    public string? GetLastErrorMsg()
    {
        if (!_libraryLoaded) return "LibraryLoadError";
        try
        {
            IntPtr ptr = NativeMethods.GetLastErrorMsg();
            return ptr == IntPtr.Zero ? null : System.Runtime.InteropServices.Marshal.PtrToStringAnsi(ptr);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge-UltraDebug] GetLastErrorMsg failed: {ex.Message}");
            return "LibraryLoadError";
        }
    }

    public void SetAudioTargetHost(string host)
    {
        if (_libraryLoaded)
        {
            try { NativeMethods.SetAudioTargetHost(host); }
            catch (Exception ex) { Console.WriteLine($"[NativeBridge-UltraDebug] SetAudioTargetHost failed: {ex.Message}"); }
        }
    }
    
    public void StartNode(string configDir, string authKey, string hostname, string localIp, bool isEphemeral)
    {
        if (!_libraryLoaded) return;

        var context = EchoLink.App.AndroidActivityInstance as Context;
        if (context == null)
        {
            Console.WriteLine("[NativeBridge-UltraDebug] FATAL: Activity context is NULL. Cannot start service.");
            return;
        }

        Console.WriteLine($"[NativeBridge-UltraDebug] Requesting EchoLinkForegroundService START. KeyLen={authKey?.Length ?? 0}");
        
        var intent = new Intent(context, typeof(EchoLinkForegroundService));
        intent.SetAction("START_SERVICE");
        intent.PutExtra("AuthKey", authKey);
        intent.PutExtra("IsEphemeral", isEphemeral);
        intent.PutExtra("Hostname", hostname);

        if (global::Android.OS.Build.VERSION.SdkInt >= global::Android.OS.BuildVersionCodes.O)
        {
            context.StartForegroundService(intent);
        }
        else
        {
            context.StartService(intent);
        }
    }

    public void StopNode()
    {
        var context = EchoLink.App.AndroidActivityInstance as Context;
        if (context != null)
        {
            var intent = new Intent(context, typeof(EchoLinkForegroundService));
            intent.SetAction("STOP_SERVICE");
            context.StartService(intent);
        }
    }

    public void LogoutNode()
    {
        if (_libraryLoaded)
        {
            try { NativeMethods.LogoutNode(); }
            catch (Exception ex) { Console.WriteLine($"[NativeBridge-UltraDebug] LogoutNode failed: {ex.Message}"); }
        }
    }

    public void SetTempSshPassword(string ip, string password)
    {
        if (_libraryLoaded)
        {
            try { NativeMethods.SetTempSshPassword(ip, password); }
            catch (Exception ex) { Console.WriteLine($"[NativeBridge-UltraDebug] SetTempSshPassword failed: {ex.Message}"); }
        }
    }

    public void RemoveTempSshPassword(string ip)
    {
        if (_libraryLoaded)
        {
            try { NativeMethods.RemoveTempSshPassword(ip); }
            catch (Exception ex) { Console.WriteLine($"[NativeBridge-UltraDebug] RemoveTempSshPassword failed: {ex.Message}"); }
        }
    }
}
