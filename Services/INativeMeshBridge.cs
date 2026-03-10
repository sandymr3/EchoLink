namespace EchoLink.Services;

public interface INativeMeshBridge
{
    string GetBackendState();
    string? GetTailscaleIp();
    string? GetLoginUrl();
    string GetPeerListJson();
    string? GetLastErrorMsg();
    void StartNode(string configDir, string authKey, string hostname, string localIp);
    void StopNode();
    void LogoutNode();
}
