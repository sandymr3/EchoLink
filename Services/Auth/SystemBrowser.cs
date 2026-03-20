using System;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Duende.IdentityModel.OidcClient.Browser;

namespace EchoLink.Services.Auth;

public class SystemBrowser : IBrowser
{
    private readonly int _port;

    public SystemBrowser(int port = 5000)
    {
        _port = port;
    }

    public async Task<BrowserResult> InvokeAsync(BrowserOptions options, CancellationToken cancellationToken = default)
    {
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        listener.Start();

        OpenBrowser(options.StartUrl);

        try
        {
            var context = await listener.GetContextAsync().WaitAsync(cancellationToken);
            var request = context.Request;
            var response = context.Response;

            string responseString = "<html><body style='font-family: sans-serif; text-align: center; margin-top: 50px;'><h1>EchoLink Login Successful</h1><p>You can close this window and return to the application.</p></body></html>";
            var buffer = Encoding.UTF8.GetBytes(responseString);
            response.ContentLength64 = buffer.Length;
            
            var responseOutput = response.OutputStream;
            await responseOutput.WriteAsync(buffer, 0, buffer.Length, cancellationToken);
            responseOutput.Close();

            return new BrowserResult
            {
                ResultType = BrowserResultType.Success,
                Response = request.Url?.ToString()
            };
        }
        catch (TaskCanceledException)
        {
            return new BrowserResult { ResultType = BrowserResultType.Timeout, Error = "Login timed out or was cancelled." };
        }
        finally
        {
            listener.Stop();
        }
    }

    private void OpenBrowser(string url)
    {
        try
        {
            if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
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
            LoggingService.Instance.Error($"[SystemBrowser] Failed to open browser: {ex.Message}");
        }
    }
}
