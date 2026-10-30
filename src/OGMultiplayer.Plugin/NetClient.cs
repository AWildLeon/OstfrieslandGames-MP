using System;
using System.Collections.Concurrent;
using System.IO;
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
    internal sealed class NetClient : IDisposable
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

        public void Connect(string host, int port, string helloLine)
        {
            if (thread != null)
            {
                throw new InvalidOperationException("NetClient is single use.");
            }
            running = true;
            thread = new Thread(() => Run(host, port, helloLine))
            {
                IsBackground = true,
                Name = "OGMultiplayer.Net"
            };
            thread.Start();
        }

        private void Run(string host, int port, string helloLine)
        {
            try
            {
                tcp = new TcpClient { NoDelay = true };
                IAsyncResult ar = tcp.BeginConnect(host, port, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(6)))
                {
                    throw new TimeoutException("Zeitueberschreitung beim Verbinden.");
                }
                tcp.EndConnect(ar);
                stream = tcp.GetStream();
                IsConnected = true;
                Send(helloLine);
                using (var reader = new StreamReader(stream, new UTF8Encoding(false)))
                {
                    while (running)
                    {
                        string line = reader.ReadLine();
                        if (line == null)
                        {
                            break;
                        }
                        if (line.Length <= Protocol.MaxLineLength)
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
