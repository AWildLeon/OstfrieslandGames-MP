using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace OGMultiplayer.Server;

/// <summary>
/// Admin console on a Unix socket (--admin-socket), for servers without a terminal such as
/// a systemd service. Every connection behaves like the server's stdin/stdout: lines sent
/// are admin commands, and it receives their replies plus the live log. Replies go only to
/// the connection that asked, never to the log, so 'players' and 'bans' may show full IPs.
/// Access control is the socket's file mode (owner only) and its directory.
/// </summary>
public sealed partial class GameServer
{
    private readonly List<AdminSession> adminSessions = new();

    private sealed class AdminSession
    {
        // a stalled reader loses old log lines instead of growing without bound
        public readonly Channel<string> Out = Channel.CreateBounded<string>(
            new BoundedChannelOptions(1000) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });
    }

    private void StartAdminSocket(CancellationToken token)
    {
        if (string.IsNullOrEmpty(options.AdminSocket))
        {
            return;
        }
        string path = Path.GetFullPath(options.AdminSocket);
        if (Encoding.UTF8.GetByteCount(path) > 107)
        {
            // sun_path holds 108 bytes including the terminator
            throw new IOException($"Admin-Socket-Pfad ist zu lang (max. 107 Bytes): {path}");
        }
        if (File.Exists(path))
        {
            // left behind by a crash; binding would fail otherwise
            File.Delete(path);
        }
        var listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        listener.Bind(new UnixDomainSocketEndPoint(path));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        listener.Listen(4);
        token.Register(() =>
        {
            listener.Close();
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
        });
        _ = Task.Run(() => AcceptAdmins(listener, token), token);
        Log($"Admin-Socket: {path}");
    }

    private async Task AcceptAdmins(Socket listener, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                socket = await listener.AcceptAsync(token);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }
            _ = Task.Run(() => ServeAdmin(socket, token), token);
        }
    }

    private async Task ServeAdmin(Socket socket, CancellationToken token)
    {
        var session = new AdminSession();
        lock (adminSessions)
        {
            adminSessions.Add(session);
        }
        using var stream = new NetworkStream(socket, ownsSocket: true);
        var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
        Task pump = Task.Run(async () =>
        {
            try
            {
                await foreach (string line in session.Out.Reader.ReadAllAsync(token))
                {
                    await writer.WriteLineAsync(line);
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // admin went away or the server is stopping
            }
        });
        try
        {
            using var reader = new StreamReader(stream, new UTF8Encoding(false), false, 1024, leaveOpen: true);
            while (await reader.ReadLineAsync(token) is string line)
            {
                if (line.Trim().Length > 0)
                {
                    session.Out.Writer.TryWrite(ExecuteAdmin(line, session));
                }
            }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException)
        {
        }
        finally
        {
            lock (adminSessions)
            {
                adminSessions.Remove(session);
            }
            // end of input (e.g. "echo status | socat ..."): send what is queued, then close
            session.Out.Writer.TryComplete();
            await pump;
        }
    }

    /// <summary>Hands a log line to every admin session except the one that caused it.</summary>
    private void ForwardLog(string line, AdminSession? except)
    {
        lock (adminSessions)
        {
            foreach (AdminSession s in adminSessions)
            {
                if (s != except)
                {
                    s.Out.Writer.TryWrite(line);
                }
            }
        }
    }
}
