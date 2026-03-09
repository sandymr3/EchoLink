using EchoLink.Services;
using System;

namespace EchoLink.Android;

public class AndroidNativeMeshBridge : INativeMeshBridge
{
    private bool _libraryLoaded = true;

    public AndroidNativeMeshBridge()
    {
        try 
        {
            // Test if we can call a simple method to verify library load
            NativeMethods.GetBackendState();
        }
        catch (DllNotFoundException ex)
        {
            Console.WriteLine($"[NativeBridge] CRITICAL: libecholink.so not found! {ex.Message}");
            _libraryLoaded = false;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[NativeBridge] Error loading library: {ex.Message}");
            _libraryLoaded = false;
        }
    }

    public string GetBackendState() => _libraryLoaded ? NativeMethods.GetBackendState() : "LibraryLoadError";
    public string? GetTailscaleIp() => _libraryLoaded ? NativeMethods.GetTailscaleIp() : null;
    public string? GetLoginUrl() => _libraryLoaded ? NativeMethods.GetLoginUrl() : null;
    public string GetPeerListJson() => _libraryLoaded ? NativeMethods.GetPeerListJson() : "[]";
    
    public void StartNode(string configDir, string authKey, string hostname)
    {
        if (_libraryLoaded)
            NativeMethods.StartEchoLinkNode(configDir, authKey, hostname);
    }

    public void StopNode()
    {
        if (_libraryLoaded)
            NativeMethods.StopEchoLinkNode();
    }

    public void LogoutNode()
    {
        if (_libraryLoaded)
            NativeMethods.LogoutNode();
    }
}
