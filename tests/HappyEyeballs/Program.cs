using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Net.Security;
using System.Net.WebSockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using jkcnsl;

var v6 = IPAddress.Parse("2001:db8::1");
var v6b = IPAddress.Parse("2001:db8::2");
var v4 = IPAddress.Parse("192.0.2.1");
var v4b = IPAddress.Parse("192.0.2.2");

await Run("DNS order alternates address families and removes duplicates", () =>
{
    Check(HappyEyeballsConnector.InterleaveAddresses(new[] { v6, v6b, v4, v4b, v6 })
        .SequenceEqual(new[] { v6, v4, v6b, v4b }));
    Check(HappyEyeballsConnector.InterleaveAddresses(new[] { v4, v4b, v6 })
        .SequenceEqual(new[] { v4, v6, v4b }));
    return Task.CompletedTask;
});

await Run("Stalled IPv6 falls back to IPv4 and cancels the stalled attempt", async () =>
{
    bool canceled = false;
    var elapsed = Stopwatch.StartNew();
    using var stream = await HappyEyeballsConnector.ConnectAsync(new[] { v6, v4 }, 443, async (ep, ct) =>
    {
        if (ep.Address.Equals(v6))
        {
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { canceled = ct.IsCancellationRequested; }
        }
        Check(elapsed.ElapsedMilliseconds >= 200, "Fallback must be staggered");
        return new MemoryStream();
    }, CancellationToken.None);
    Check(canceled);
    Check(elapsed.Elapsed < TimeSpan.FromSeconds(3), "Must not wait for a socket timeout");
});

await Run("Healthy first address avoids starting the alternate", async () =>
{
    int starts = 0;
    using var stream = await HappyEyeballsConnector.ConnectAsync(new[] { v6, v4 }, 443, (ep, ct) =>
    {
        starts++;
        Check(ep.Address.Equals(v6));
        return Task.FromResult<Stream>(new MemoryStream());
    }, CancellationToken.None);
    Check(starts == 1);
});

await Run("Stalled IPv4 also falls back to IPv6", async () =>
{
    using var stream = await HappyEyeballsConnector.ConnectAsync(new[] { v4, v6 }, 443, async (ep, ct) =>
    {
        if (ep.Address.Equals(v4)) await Task.Delay(Timeout.Infinite, ct);
        return new MemoryStream();
    }, CancellationToken.None);
});

await Run("Single-family hosts try later addresses", async () =>
{
    foreach (var addresses in new[] { new[] { v6, v6b }, new[] { v4, v4b } })
    {
        using var stream = await HappyEyeballsConnector.ConnectAsync(addresses, 443, async (ep, ct) =>
        {
            if (ep.Address.Equals(addresses[0])) await Task.Delay(Timeout.Infinite, ct);
            return new MemoryStream();
        }, CancellationToken.None);
    }
});

await Run("Failure of all addresses reports the connection error", async () =>
{
    try
    {
        await HappyEyeballsConnector.ConnectAsync(new[] { v6, v4 }, 443,
            (ep, ct) => Task.FromException<Stream>(new SocketException((int)SocketError.ConnectionRefused)),
            CancellationToken.None);
        throw new Exception("Expected a connection failure");
    }
    catch (HttpRequestException e) { Check(e.InnerException is SocketException); }
});

await Run("Overall timeout cancels active and delayed attempts", async () =>
{
    int starts = 0, canceled = 0;
    using var cts = new CancellationTokenSource(100);
    try
    {
        await HappyEyeballsConnector.ConnectAsync(new[] { v6, v4 }, 443, async (ep, ct) =>
        {
            starts++;
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { if (ct.IsCancellationRequested) canceled++; }
            return new MemoryStream();
        }, cts.Token);
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { Check(starts == 1 && canceled == 1); }
});

await Run("A concurrent losing success is disposed and the winner stays open", async () =>
{
    var first = new TaskCompletionSource<Stream>(TaskCreationOptions.RunContinuationsAsynchronously);
    var stream1 = new TrackingStream();
    var stream2 = new TrackingStream();
    using var winner = await HappyEyeballsConnector.ConnectAsync(new[] { v6, v4 }, 443, (ep, ct) =>
    {
        if (ep.Address.Equals(v6)) return first.Task;
        first.SetResult(stream1);
        return Task.FromResult<Stream>(stream2);
    }, CancellationToken.None);
    Check(!((TrackingStream)winner).Disposed);
    Check(ReferenceEquals(winner, stream1) ? stream2.Disposed : stream1.Disposed);
});

await Run("Empty DNS result fails and pre-canceled calls do not start connections", async () =>
{
    try
    {
        await HappyEyeballsConnector.ConnectAsync(Array.Empty<IPAddress>(), 443,
            (ep, ct) => throw new Exception("Must not connect"), CancellationToken.None);
        throw new Exception("Expected DNS failure");
    }
    catch (SocketException) { }
    try
    {
        await HappyEyeballsConnector.ConnectAsync(new[] { v6 }, 443,
            (ep, ct) => throw new Exception("Must not connect"), new CancellationToken(true));
        throw new Exception("Expected cancellation");
    }
    catch (OperationCanceledException) { }
});

await Run("Real WebSocket handshake retains headers, subprotocol, and frame delivery", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Task server = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
        using var stream = tcp.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, false, leaveOpen: true);
        string request = await reader.ReadLineAsync(timeout.Token);
        Check(request == "GET /watch/test HTTP/1.1");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (string line; !string.IsNullOrEmpty(line = await reader.ReadLineAsync(timeout.Token));)
        {
            int colon = line.IndexOf(':');
            headers[line[..colon]] = line[(colon + 1)..].Trim();
        }
        Check(headers["Origin"] == "https://example.test");
        Check(headers["User-Agent"] == "jkcnsl-test");
        Check(headers["Sec-WebSocket-Protocol"] == "msg.nicovideo.jp#json");
        string accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(
            headers["Sec-WebSocket-Key"] + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
        await stream.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: " + accept +
            "\r\nSec-WebSocket-Protocol: msg.nicovideo.jp#json\r\n\r\n"), timeout.Token);
        await stream.WriteAsync(new byte[] { 0x81, 2, (byte)'o', (byte)'k' }, timeout.Token);
    });
    using var invoker = HappyEyeballsConnector.CreateInvoker();
    using var ws = new ClientWebSocket();
    ws.Options.SetRequestHeader("Origin", "https://example.test");
    ws.Options.SetRequestHeader("User-Agent", "jkcnsl-test");
    ws.Options.AddSubProtocol("msg.nicovideo.jp#json");
    await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/watch/test"), invoker, timeout.Token);
    byte[] buffer = new byte[32];
    var frame = await ws.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
    Check(Encoding.ASCII.GetString(buffer, 0, frame.Count) == "ok");
    await server;
});

await Run("TLS still rejects an untrusted server certificate", async () =>
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    using var key = RSA.Create(2048);
    var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
    // Import the private key so Windows Schannel can use it as a server credential.
    using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null);
    using var listener = new TcpListener(IPAddress.Loopback, 0);
    listener.Start();
    int port = ((IPEndPoint)listener.LocalEndpoint).Port;
    Task server = Task.Run(async () =>
    {
        using var tcp = await listener.AcceptTcpClientAsync(timeout.Token);
        using var ssl = new SslStream(tcp.GetStream());
        try
        {
            await ssl.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, timeout.Token);
            _ = await ssl.ReadAsync(new byte[1], timeout.Token);
        }
        catch (Exception e) when (e is IOException || e is AuthenticationException) { }
    });
    using var invoker = HappyEyeballsConnector.CreateInvoker();
    using var ws = new ClientWebSocket();
    try
    {
        await ws.ConnectAsync(new Uri($"wss://127.0.0.1:{port}/"), invoker, timeout.Token);
        throw new Exception("An untrusted certificate was accepted");
    }
    catch (WebSocketException e)
    {
        Exception cause = e;
        while (cause != null && cause is not AuthenticationException) cause = cause.InnerException;
        Check(cause is AuthenticationException, "Expected a TLS validation failure: " + e);
    }
    await server;
});

Console.WriteLine("All 11 tests passed.");

static void Check(bool value, string message = "Assertion failed")
{
    if (!value) throw new Exception(message);
}

static async Task Run(string name, Func<Task> test)
{
    await test().WaitAsync(TimeSpan.FromSeconds(8));
    Console.WriteLine("PASS " + name);
}

class TrackingStream : MemoryStream
{
    public bool Disposed { get; private set; }
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
