using System.Runtime.InteropServices;
using System.Text;

namespace Outspread.Core;

/// <summary>
/// Where a sync token lives on this machine: the credential store the OS already has,
/// rather than a file in the board folder or a line in a config an agent might read.
/// </summary>
/// <remarks>
/// The token is what lets a device overwrite the board. Keeping it out of the repository
/// folder means a board copied to a USB stick, zipped for a backup, or read by an agent
/// with ordinary file access does not carry the key with it.
///
/// <see cref="EnvironmentVariable"/> is the way in for a single command, not the storage:
/// a shell variable is visible to every child process, and an agent runs children.
/// </remarks>
public static class SyncCredentials
{
    /// <summary>Target name in the store. It is part of the contract: renaming it logs every device out.</summary>
    public const string Target = "outspread.sync.github";

    public const string EnvironmentVariable = "OUTSPREAD_SYNC_TOKEN";

    /// <summary>Read after the new name, for a round started from a shell that still exports the old one.</summary>
    public const string LegacyEnvironmentVariable = "TANKAN_SYNC_TOKEN";

    /// <summary>
    /// The target name used before the product was renamed. The rename left a real token
    /// stranded under the old name, which reads as "no credential" — that is, the rename
    /// logged every already-paired machine out without saying so.
    /// </summary>
    public const string LegacyTarget = "tankan.sync.github";

    /// <summary>The old name is copied up, never deleted: an earlier build may still be reading it.</summary>
    public static string? LastMigration { get; private set; }

    private static string? ReadRaw(string target)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        if (!CredReadW(target, CredentialTypeGeneric, 0, out var buffer))
        {
            return null;
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeCredential>(buffer);

            if (credential.CredentialBlobSize <= 0 || credential.CredentialBlob == IntPtr.Zero)
            {
                return null;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);

            return DecodeStored(bytes);
        }
        finally
        {
            CredFree(buffer);
        }
    }

    private const int CredentialTypeGeneric = 1;

    /// <summary>CRED_PERSIST_LOCAL_MACHINE: survives a reboot, stays visible to this user only.</summary>
    private const int PersistLocalMachine = 1;

    /// <summary>
    /// Returns the token, or null when nothing is stored. A token passed through the
    /// environment wins for this one call, which is how a rehearsal can be run without
    /// rewriting what the board will use tomorrow.
    /// </summary>
    public static string? Read(string target = Target)
    {
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable)
            ?? Environment.GetEnvironmentVariable(LegacyEnvironmentVariable);

        if (!string.IsNullOrWhiteSpace(fromEnvironment))
        {
            return Normalize(fromEnvironment);
        }

        var stored = ReadRaw(target);

        if (stored is not null)
        {
            return stored;
        }

        if (target != Target)
        {
            return null;
        }

        var legacy = ReadRaw(LegacyTarget);

        if (legacy is null)
        {
            return null;
        }

        // Copied up, not moved: the old entry stays where an earlier build left it, because
        // deleting a credential this program no longer owns the meaning of is not ours to do.
        var adopted = Write(legacy, user: null, target: Target);

        LastMigration = adopted
            ? $"令牌从旧名字 {LegacyTarget} 认到了 {Target}（旧那条仍在凭据管理器里，没删）"
            : $"找到了旧名字 {LegacyTarget} 下的令牌，但没能复制到 {Target}：这次先用旧的，下次还得再认一遍";

        return legacy;
    }

    public static bool Write(string token, string? user = null, string target = Target)
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        var bytes = Encoding.UTF8.GetBytes(token.Trim());
        var blob = Marshal.AllocHGlobal(bytes.Length);

        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);

            var credential = new NativeCredential
            {
                Flags = 0,
                Type = CredentialTypeGeneric,
                TargetName = target,
                Comment = "「铺陈」画板的同步令牌（只认这一个仓库）",
                LastWritten = 0,
                CredentialBlobSize = bytes.Length,
                CredentialBlob = blob,
                Persist = PersistLocalMachine,
                AttributeCount = 0,
                Attributes = IntPtr.Zero,
                TargetAlias = null,
                UserName = user ?? "outspread",
            };

            return CredWriteW(ref credential, 0);
        }
        finally
        {
            Marshal.FreeHGlobal(blob);
        }
    }

    public static bool Delete(string target = Target)
        => OperatingSystem.IsWindows() && CredDeleteW(target, CredentialTypeGeneric, 0);

    /// <summary>
    /// The blob is whatever the writer chose: cmdkey stores what it was given as UTF-16, so
    /// decoding it as UTF-8 yields the token with a NUL between every character, and an HTTP
    /// header refuses that deep inside the request. The shape tells them apart — a UTF-16
    /// generic credential from Windows always has zero bytes on the odd positions.
    /// </summary>
    public static string? DecodeStored(byte[] bytes)
    {
        var looksUtf16 = bytes.Length >= 4 && bytes.Length % 2 == 0;

        if (looksUtf16)
        {
            for (var index = 1; index < bytes.Length; index += 2)
            {
                if (bytes[index] != 0)
                {
                    looksUtf16 = false;

                    break;
                }
            }
        }

        var text = looksUtf16
            ? Encoding.Unicode.GetString(bytes)
            : Encoding.UTF8.GetString(bytes);

        return Normalize(text);
    }

    /// <summary>
    /// A token pasted into cmdkey or piped from a file arrives with the newline that came with
    /// it, and a header value carrying one throws inside the request construction. The trim
    /// happens here so both sources are held to the same shape.
    /// </summary>
    public static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim('\r', '\n', ' ', '\t', '\0');

    /// <summary>
    /// Where <see cref="Read"/> got its answer. A token that came from the environment is not
    /// stored, and a status line that says "已存" about it would promise a reboot survives it.
    /// </summary>
    public static string Origin(string target = Target)
    {
        if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)
                ?? Environment.GetEnvironmentVariable(LegacyEnvironmentVariable)))
        {
            return "环境变量（只在这一次调用里有效，没有落盘）";
        }

        if (ReadRaw(target) is not null)
        {
            return "凭据管理器";
        }

        return ReadRaw(LegacyTarget) is not null ? "凭据管理器（旧名字）" : "没有";
    }

    /// <summary>A short, non-secret hint for a status line: enough to tell two tokens apart, never the token.</summary>
    public static string? Fingerprint(string? token)
    {
        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var digest = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(token));

        return Convert.ToHexString(digest[..4]).ToLowerInvariant();
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeCredential
    {
        public int Flags;
        public int Type;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string TargetName;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? Comment;

        public long LastWritten;
        public int CredentialBlobSize;
        public IntPtr CredentialBlob;
        public int Persist;
        public int AttributeCount;
        public IntPtr Attributes;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? TargetAlias;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, int type, int flags, out IntPtr credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref NativeCredential credential, int flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, int type, int flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(IntPtr credential);
}
