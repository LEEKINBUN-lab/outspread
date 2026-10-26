using System.Runtime.InteropServices;

namespace Outspread.Board;

/// <summary>Asks the shell to take a file away the way Explorer would: undoable in the Recycle Bin.</summary>
internal static class ShellRecycle
{
    private const uint FileOperationDelete = 0x0003;

    private const ushort AllowUndo = 0x0040;

    private const ushort Silent = 0x0004;

    private const ushort NoConfirmation = 0x0010;

    private const ushort NoErrorUi = 0x0400;

    public static bool TryDeleteToRecycleBin(string path)
    {
        if (!File.Exists(path) && !Directory.Exists(path))
        {
            return true;
        }

        var operation = new FileOperation
        {
            hwnd = IntPtr.Zero,
            wFunc = FileOperationDelete,
            pFrom = path + '\0' + '\0',
            fFlags = (ushort)(AllowUndo | Silent | NoConfirmation | NoErrorUi),
        };

        return SHFileOperationW(ref operation) == 0 && !operation.fAnyOperationsAborted;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FileOperation
    {
        public IntPtr hwnd;

        public uint wFunc;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string pFrom;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? pTo;

        public ushort fFlags;

        [MarshalAs(UnmanagedType.Bool)]
        public bool fAnyOperationsAborted;

        public IntPtr hNameMappings;

        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszProgressTitle;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHFileOperationW(ref FileOperation lpFileOp);
}
