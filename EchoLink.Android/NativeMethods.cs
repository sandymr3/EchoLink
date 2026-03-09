using System;
using System.Runtime.InteropServices;

namespace EchoLink.Android;

public static class NativeMethods
{
    private const string LibraryName = "echolink"; // libecholink.so

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern int StartEchoLinkNode(string configDir, string authKey, string hostname);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void StopEchoLinkNode();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetBackendState();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetTailscaleIp();
    
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetLoginUrl();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern string GetPeerListJson();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LogoutNode();
}
