using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OGMultiplayer.Server;

/// <summary>Everything admins change at runtime that should survive a restart.</summary>
public sealed class ServerState
{
    public int Version { get; set; } = 1;

    public List<BanEntry> Bans { get; set; } = new();

    /// <summary>Key (Protocol.ServerKey) of the password set with the admin command; null = configured password.</summary>
    public string? PasswordKeyOverride { get; set; }

    /// <summary>Only read from old state files (plain text); converted to a key on load.</summary>
    public string? PasswordOverride { get; set; }

    /// <summary>Password switched off with the admin command (wins over the configured one).</summary>
    public bool PasswordDisabled { get; set; }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Loads the state file; a missing file is an empty state, a broken one is an error.</summary>
    public static ServerState Load(string path)
    {
        if (!File.Exists(path))
        {
            return new ServerState();
        }
        string json = File.ReadAllText(path);
        ServerState? state;
        try
        {
            state = JsonSerializer.Deserialize<ServerState>(json, JsonOptions);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"State-Datei '{path}' ist kaputt: {ex.Message}", ex);
        }
        if (state == null)
        {
            throw new InvalidDataException($"State-Datei '{path}' ist leer.");
        }
        state.Bans ??= new List<BanEntry>();
        state.Bans.RemoveAll(b => b == null || !IPAddress.TryParse(b.Ip, out _));
        return state;
    }

    /// <summary>Writes atomically (temp file + rename), readable only by the owner.</summary>
    public void Save(string path)
    {
        string full = Path.GetFullPath(path);
        string? dir = Path.GetDirectoryName(full);
        if (!string.IsNullOrEmpty(dir))
        {
            Directory.CreateDirectory(dir);
        }
        string tmp = full + ".tmp";
        var fileOptions = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            fileOptions.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }
        using (var stream = new FileStream(tmp, fileOptions))
        {
            JsonSerializer.Serialize(stream, this, JsonOptions);
            stream.Flush(true);
        }
        File.Move(tmp, full, overwrite: true);
    }

    public Dictionary<IPAddress, BanEntry> BanIndex()
    {
        var index = new Dictionary<IPAddress, BanEntry>();
        foreach (BanEntry b in Bans)
        {
            index[Normalize(IPAddress.Parse(b.Ip))] = b;
        }
        return index;
    }

    public static IPAddress Normalize(IPAddress ip) => ip.IsIPv4MappedToIPv6 ? ip.MapToIPv4() : ip;
}

public sealed class BanEntry
{
    public string Ip { get; set; } = string.Empty;

    public string? Reason { get; set; }

    /// <summary>Initials of the banned player, if banned by player id.</summary>
    public string? Player { get; set; }

    public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
}
