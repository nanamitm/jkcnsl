using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

namespace jkcnsl
{
    // Race TCP connections only. The handler still handles proxies, TLS/SNI,
    // certificate validation, and the WebSocket handshake using the original URI.
    static class HappyEyeballsConnector
    {
        const int ConnectionAttemptDelayMilliseconds = 250;

        public static HttpMessageInvoker CreateInvoker()
        {
            return new HttpMessageInvoker(new SocketsHttpHandler
            {
                UseCookies = false,
                ConnectCallback = ConnectTcpAsync
            });
        }

        static async ValueTask<Stream> ConnectTcpAsync(SocketsHttpConnectionContext context, CancellationToken ct)
        {
            IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, ct);
            addresses = InterleaveAddresses(addresses.Where(a =>
                (a.AddressFamily == AddressFamily.InterNetwork && Socket.OSSupportsIPv4) ||
                (a.AddressFamily == AddressFamily.InterNetworkV6 && Socket.OSSupportsIPv6))).ToArray();
            return await ConnectAsync(addresses, context.DnsEndPoint.Port, ConnectAddressAsync, ct);
        }

        // Keep DNS preference, but try the other address family second.
        internal static IEnumerable<IPAddress> InterleaveAddresses(IEnumerable<IPAddress> addresses)
        {
            IPAddress[] distinct = addresses.Distinct().ToArray();
            if (distinct.Length == 0) yield break;
            var preferred = new Queue<IPAddress>(distinct.Where(a => a.AddressFamily == distinct[0].AddressFamily));
            var alternate = new Queue<IPAddress>(distinct.Where(a => a.AddressFamily != distinct[0].AddressFamily));
            while (preferred.Count > 0 || alternate.Count > 0)
            {
                if (preferred.Count > 0) yield return preferred.Dequeue();
                if (alternate.Count > 0) yield return alternate.Dequeue();
            }
        }

        internal static async Task<Stream> ConnectAsync(IReadOnlyList<IPAddress> addresses, int port,
            Func<IPEndPoint, CancellationToken, Task<Stream>> connectAddressAsync, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

            using var attemptsCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var attempts = new List<Task<Stream>>();
            Stream winner = null;
            try
            {
                var pending = new List<Task<Stream>>();
                Task nextAttemptDelay = null;
                int next = 0;
                Exception lastError = null;
                for (;;)
                {
                    ct.ThrowIfCancellationRequested();
                    if (nextAttemptDelay == null && next < addresses.Count)
                    {
                        Task<Stream> attempt = AttemptAsync(addresses[next++]);
                        attempts.Add(attempt);
                        pending.Add(attempt);
                        if (next < addresses.Count)
                        {
                            nextAttemptDelay = Task.Delay(ConnectionAttemptDelayMilliseconds, attemptsCts.Token);
                        }
                    }
                    if (pending.Count == 0) break;

                    var waiting = new List<Task>(pending);
                    if (nextAttemptDelay != null) waiting.Add(nextAttemptDelay);
                    Task completed = await Task.WhenAny(waiting);
                    if (completed == nextAttemptDelay)
                    {
                        nextAttemptDelay = null;
                        continue;
                    }
                    var completedAttempt = (Task<Stream>)completed;
                    pending.Remove(completedAttempt);
                    try
                    {
                        Stream stream = await completedAttempt;
                        if (ct.IsCancellationRequested)
                        {
                            stream.Dispose();
                            ct.ThrowIfCancellationRequested();
                        }
                        winner = stream;
                        return winner;
                    }
                    catch (Exception e) when (e is SocketException || e is IOException)
                    {
                        lastError = e;
                        // Do not wait out the delay after a failure; start the next address now.
                        nextAttemptDelay = null;
                    }
                }
                throw new HttpRequestException("Could not connect to any resolved address.", lastError);
            }
            finally
            {
                // Observe every attempt, including another connection that may
                // have succeeded at the same time. Only the winner stays open.
                attemptsCts.Cancel();
                foreach (Task<Stream> attempt in attempts)
                {
                    try
                    {
                        Stream stream = await attempt;
                        if (!ReferenceEquals(stream, winner)) stream.Dispose();
                    }
                    catch
                    {
                        // A losing attempt's error must not hide the result or leak the winner.
                    }
                }
            }

            async Task<Stream> AttemptAsync(IPAddress address)
            {
                return await connectAddressAsync(new IPEndPoint(address, port), attemptsCts.Token);
            }
        }

        static async Task<Stream> ConnectAddressAsync(IPEndPoint endPoint, CancellationToken ct)
        {
            var socket = new Socket(endPoint.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            try
            {
                await socket.ConnectAsync(endPoint, ct);
                return new NetworkStream(socket, ownsSocket: true);
            }
            catch
            {
                socket.Dispose();
                throw;
            }
        }
    }
}
