using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace EchoLink.Services.Auth;

public class DefaultAuthPlatformHandler : IAuthPlatformHandler
{
    public async Task<string?> LoginAsync()
    {
        // PC Flow: Start a temporary local listener, then open the browser
        using var listener = new HttpListener();
        int port = new Random().Next(45000, 46000); 
        string redirectUrl = $"http://127.0.0.1:{port}/";
        
        listener.Prefixes.Add(redirectUrl);
        listener.Start();

        var uri = $"https://api.echo-link.app/auth/login?device=pc&port={port}";
        OpenBrowser(uri);

        // Wait for the Go middleware to redirect the browser back to us
        var context = await listener.GetContextAsync();
        var request = context.Request;
        var response = context.Response;
        
        string? authKey = request.QueryString["key"];

        // Send a success message to the browser so the user can close the tab
        string responseString = "<html><body style='font-family: sans-serif; text-align: center; margin-top: 50px;'><h1>Login Successful</h1><p>You can close this tab and return to EchoLink.</p><script>window.close();</script></body></html>";
        var buffer = Encoding.UTF8.GetBytes(responseString);
        response.ContentLength64 = buffer.Length;
        await response.OutputStream.WriteAsync(buffer, 0, buffer.Length);
        response.OutputStream.Close();
        listener.Stop();

        return authKey;
    }

    private void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo("cmd", $"/c start {url.Replace("&", "^&")}") { CreateNoWindow = true });
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
            {
                Process.Start("xdg-open", url);
            }
            else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
            {
                Process.Start("open", url);
            }
        }
        catch (Exception ex)
        {
            LoggingService.Instance.Error($"[AuthService] Failed to open browser: {ex.Message}");
        }
    }
}
