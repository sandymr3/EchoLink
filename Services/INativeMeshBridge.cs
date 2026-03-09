namespace EchoLink.Services;

public interface INativeMeshBridge
{
    string GetBackendState();
    string? GetTailscaleIp();
    string? GetLoginUrl();
    string GetPeerListJson();
    void StartNode(string configDir, string authKey, string hostname);
    void StopNode();
    void LogoutNode();
}
