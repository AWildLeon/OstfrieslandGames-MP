using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;

namespace OGMultiplayer.Plugin
{
    /// <summary>
    /// Minimal TCP line client. Network I/O runs on a background thread;
    /// received lines are queued and drained on the Unity main thread.
    /// One instance per connection attempt.
    /// </summary>
    /// <summary>Line based connection to a server (real TCP or the local bot server).</summary>
    internal interface ITransport : IDisposable
    {
        string LastError { get; }

        void Send(string line);

        bool TryReceive(out string line);

        /// <summary>Called every frame on the main thread.</summary>
        void Tick();
    }

    internal sealed class NetClient : ITransport
    {
        public const string DisconnectedMarker = "\u0000DISCONNECTED";

        private readonly ConcurrentQueue<string> incoming = new ConcurrentQueue<string>();
        private readonly object writeLock = new object();
        private TcpClient tcp;
        private NetworkStream stream;
        private Thread thread;
        private volatile bool running;

        public bool IsConnected { get; private set; }

        public string LastError { get; private set; }

        public void Connect(string host, int port)
        {
            if (thread != null)
            {
                throw new InvalidOperationException("NetClient is single use.");
            }
            running = true;
            thread = new Thread(() => Run(host, port))
            {
                IsBackground = true,
                Name = "OGMultiplayer.Net"
            };
            thread.Start();
        }

        private void Run(string host, int port)
        {
            try
            {
                ConnectAny(host, port, TimeSpan.FromSeconds(6));
                stream = tcp.GetStream();
                IsConnected = true;
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    while (running)
                    {
                        string line = reader.ReadLine();
                        if (line == null)
                        {
                            break;
                        }
                        if (line.Length <= Protocol.MaxServerLineLength)
                        {
                            incoming.Enqueue(line);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                if (running)
                {
                    LastError = ex.Message;
                }
            }
            finally
            {
                IsConnected = false;
                if (running)
                {
                    incoming.Enqueue(DisconnectedMarker);
                }
                running = false;
                CloseSocket();
            }
        }

        /// <summary>
        /// Tries every address of the host in the resolver's order (IPv6 first where the
        /// system prefers it), each with a socket of its own family: a plain TcpClient is
        /// IPv4 only and silently skips the AAAA records.
        /// </summary>
        private void ConnectAny(string host, int port, TimeSpan timeout)
        {
            DateTime deadline = DateTime.UtcNow + timeout;
            string name = host.Trim().TrimStart('[').TrimEnd(']');
            IPAddress[] addresses = IPAddress.TryParse(name, out IPAddress literal)
                ? new[] { literal }
                : Array.FindAll(Dns.GetHostAddresses(name),
                    a => a.AddressFamily == AddressFamily.InterNetwork || a.AddressFamily == AddressFamily.InterNetworkV6);
            Exception last = null;
            for (int i = 0; i < addresses.Length && running; i++)
            {
                TimeSpan left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero)
                {
                    break;
                }
                // an unreachable address must leave time for the next one
                TimeSpan slice = i < addresses.Length - 1 && left > TimeSpan.FromSeconds(3) ? TimeSpan.FromSeconds(3) : left;
                var client = new TcpClient(addresses[i].AddressFamily) { NoDelay = true };
                tcp = client;
                try
                {
                    IAsyncResult ar = client.BeginConnect(addresses[i], port, null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(slice))
                    {
                        throw new TimeoutException("Zeitüberschreitung beim Verbinden.");
                    }
                    client.EndConnect(ar);
                    return;
                }
                catch (Exception ex) when (ex is SocketException || ex is TimeoutException || ex is ObjectDisposedException)
                {
                    last = ex;
                    client.Close();
                }
            }
            throw last ?? new SocketException((int)SocketError.HostNotFound);
        }

        public void Tick()
        {
        }

        public bool TryReceive(out string line)
        {
            return incoming.TryDequeue(out line);
        }

        public void Send(string line)
        {
            NetworkStream s = stream;
            if (s == null || !IsConnected)
            {
                return;
            }
            byte[] bytes = Encoding.UTF8.GetBytes(line + "\n");
            try
            {
                lock (writeLock)
                {
                    s.Write(bytes, 0, bytes.Length);
                    s.Flush();
                }
            }
            catch (Exception ex)
            {
                LastError = ex.Message;
                CloseSocket();
            }
        }

        private void CloseSocket()
        {
            try
            {
                stream?.Close();
            }
            catch
            {
            }
            try
            {
                tcp?.Close();
            }
            catch
            {
            }
            stream = null;
        }

        public void Dispose()
        {
            running = false;
            IsConnected = false;
            CloseSocket();
            while (incoming.TryDequeue(out _))
            {
            }
        }
    }
}
