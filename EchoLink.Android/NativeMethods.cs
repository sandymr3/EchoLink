using System;
using System.Runtime.InteropServices;

namespace EchoLink.Android;

public static class NativeMethods
{
    private const string LibraryName = "echolink"; // libecholink.so

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern int StartEchoLinkNode(string configDir, string authKey, string hostname, string localIp, int isEphemeral);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void StopEchoLinkNode();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    [return: MarshalAs(UnmanagedType.LPStr)]
    public static extern IntPtr GetBackendState();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetTailscaleIp();
    
    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetLoginUrl();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetPeerListJson();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern IntPtr GetLastErrorMsg();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void SetAudioTargetHost(string host);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl)]
    public static extern void LogoutNode();

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void SetTempSshPassword(string ip, string password);

    [DllImport(LibraryName, CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern void RemoveTempSshPassword(string ip);
}
