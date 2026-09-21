using System.Net;
using System.Net.Sockets;
using System.Text;
using LittleTools.Assistant.Services;

internal static class NetworkTests
{
    internal static async Task RunAsync(Action<string, bool> check)
    {
        var oldProxy = HttpClient.DefaultProxy;
        var proxy = new RejectingProxy();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            HttpClient.DefaultProxy = proxy;
            var server = Task.Run(async () =>
            {
                using var socket = await listener.AcceptTcpClientAsync(deadline.Token);
                using var stream = socket.GetStream();
                using var reader = new StreamReader(stream, leaveOpen: true);
                while (!string.IsNullOrEmpty(await reader.ReadLineAsync(deadline.Token))) { }
                await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok"), deadline.Token);
            });
            using var client = DomesticApiHttpClient.Create(TimeSpan.FromSeconds(5));
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var result = await client.GetStringAsync($"http://127.0.0.1:{port}/", deadline.Token);
            await server;
            check("Domestic API bypasses broken system proxy", result == "ok" && !proxy.Used);
        }
        finally { HttpClient.DefaultProxy = oldProxy; }
    }

    private sealed class RejectingProxy : IWebProxy
    {
        public bool Used { get; private set; }
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri destination) { Used = true; throw new InvalidOperationException("Stale proxy used"); }
        public bool IsBypassed(Uri host) { Used = true; return false; }
    }
}
