using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CetaFae.Probe;

/// <summary>Only completed resting-chat text belongs here; window guidance stays ephemeral.</summary>
internal sealed class ChatHistoryStore(GuideOptions options, string? file = null)
{
    internal const int MaxBytes = 16 * 1024 * 1024;
    internal sealed record Entry(ChatTurn Turn, string Connection);
    sealed record Archive(int Version, Entry[] Entries);
    readonly string _file = file ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CetaFae", "chat-history.dat");
    readonly string _connection = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        $"{options.BaseUrl.AbsoluteUri}\n{options.Model}\n{options.IncludeImage}")));
    readonly List<Entry> _entries = [];
    bool _loaded;
    bool _readable;
    string? _revision;
    internal IReadOnlyList<ChatTurn> Turns => _entries.Select(entry => entry.Turn).ToArray();
    internal IReadOnlyList<ChatTurn> Context => _entries.AsEnumerable().Reverse()
        .TakeWhile(entry => entry.Connection == _connection).Take(FreeChatSession.MaxTurns)
        .Reverse().Select(entry => entry.Turn).ToArray();
    static bool StorageFailure(Exception ex) => ex is IOException or InvalidDataException or UnauthorizedAccessException or
        CryptographicException or JsonException or ArgumentException;

    void CheckPath(string? file = null)
    {
        for (var path = Path.GetFullPath(file ?? _file); path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("chat_path_not_regular");
    }

    FileStream Lock()
    {
        CheckPath(); CheckPath(_file + ".lock");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
        return new FileStream(_file + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
    }

    string? Revision()
    {
        if (!File.Exists(_file)) return null;
        using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > MaxBytes) throw new InvalidDataException("chat_archive_size");
        return Convert.ToHexString(SHA256.HashData(stream));
    }

    static void Validate(ChatTurn turn)
    {
        if (turn is null || turn.RequiresWindow || string.IsNullOrWhiteSpace(turn.Question) ||
            turn.Question.Length > FreeChatSession.MaxQuestionLength || turn.Question.Any(char.IsControl))
            throw new InvalidDataException("chat_archive_turn");
        GuidePrivacy.EnsureTextSafe(turn.Question);
        FreeChatSession.ValidateReply(turn.Reply);
    }

    internal bool Load()
    {
        if (_loaded) return _readable;
        _loaded = true;
        try
        {
            using var lease = Lock();
            if (!File.Exists(_file)) return _readable = !Directory.Exists(_file);
            using var stream = new FileStream(_file, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (stream.Length is <= 0 or > MaxBytes) return false;
            var bytes = new byte[(int)stream.Length]; stream.ReadExactly(bytes);
            var plain = ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser);
            try
            {
                var archive = JsonSerializer.Deserialize<Archive>(plain);
                if (archive is not { Version: 1, Entries: not null }) return false;
                foreach (var entry in archive.Entries)
                {
                    if (entry is null || entry.Connection is not { Length: 64 } || !entry.Connection.All(Uri.IsHexDigit))
                        throw new InvalidDataException("chat_archive_connection");
                    Validate(entry.Turn);
                }
                _entries.AddRange(archive.Entries);
                _revision = Convert.ToHexString(SHA256.HashData(bytes));
                return _readable = true;
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }

    internal bool Append(ChatTurn turn)
    {
        if (!Load()) return false; // A damaged archive must never be overwritten with an empty history.
        var temporary = _file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            Validate(turn);
            using var lease = Lock();
            // Another instance may have appended or cleared while this window stayed open.
            if (Revision() != _revision) return false;
            var entries = _entries.Append(new Entry(turn, _connection)).ToArray();
            var plain = JsonSerializer.SerializeToUtf8Bytes(new Archive(1, entries));
            byte[] bytes;
            try
            {
                if (plain.Length > MaxBytes - 1024) return false;
                bytes = ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
            }
            finally { CryptographicOperations.ZeroMemory(plain); }
            if (bytes.Length > MaxBytes) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_file))!);
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, _file, overwrite: true);
            _revision = Convert.ToHexString(SHA256.HashData(bytes));
            _entries.Add(entries[^1]);
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (StorageFailure(ex)) { } }
    }

    internal bool Clear()
    {
        try
        {
            using var lease = Lock();
            File.Delete(_file);
            _revision = null;
            _entries.Clear(); _loaded = _readable = true;
            return true;
        }
        catch (Exception ex) when (StorageFailure(ex)) { return false; }
    }
}
