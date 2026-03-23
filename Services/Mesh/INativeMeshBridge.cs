namespace EchoLink.Services;

public interface INativeMeshBridge
{
    string GetBackendState();
    string? GetTailscaleIp();
    string? GetLoginUrl();
    string GetPeerListJson();
    string? GetLastErrorMsg();
    void SetAudioTargetHost(string host);
    void StartNode(string configDir, string authKey, string hostname, string localIp, bool isEphemeral);
    void StopNode();
    void LogoutNode();
    void SetTempSshPassword(string ip, string password);
    void RemoveTempSshPassword(string ip);
}
