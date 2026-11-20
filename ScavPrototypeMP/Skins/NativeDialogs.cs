using System;
using System.Runtime.InteropServices;
using System.Text;
using UnityEngine;

// the operating system's own folder and file dialogs on Windows. elsewhere they are unavailable and the in-game browser is used.
// both block the game until they close.
internal static class NativeDialogs {
    private const int MaxPath = 260;
    private const uint BrowseReturnOnlyFileSystemDirs = 0x0001;
    private const uint BrowseEditBox = 0x0010;
    private const uint BrowseNewDialogStyle = 0x0040;
    private const uint BrowseInitialized = 1;
    private const uint BrowseSetSelection = 0x0467;
    private const int OpenFileMustExist = 0x1000;
    private const int OpenPathMustExist = 0x0800;
    private const int OpenNoChangeDir = 0x0008;
    private const int OpenExplorer = 0x00080000;
    private const int FileBufferChars = 1024;

    internal static bool Available =>
        Application.platform == RuntimePlatform.WindowsPlayer || Application.platform == RuntimePlatform.WindowsEditor;

    // false: the dialog could not be shown, use the in-game browser. true with a null path: the player cancelled.
    internal static bool TryPickFolder(string title, string startDirectory, out string path) {
        path = null;
        if (!Available) {
            return false;
        }

        try {
            return PickFolder(title, startDirectory, out path);
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning("Folder dialog failed: " + ex.Message);
            return false;
        }
    }

    // filter is Windows style: "Skin files\0*.json;*.zip\0All files\0*.*\0"
    internal static bool TryPickFile(string title, string startDirectory, string filter, out string path) {
        path = null;
        if (!Available) {
            return false;
        }

        try {
            return PickFile(title, startDirectory, filter, out path);
        } catch (Exception ex) {
            SpriteReplacerPlugin.Log.LogWarning("File dialog failed: " + ex.Message);
            return false;
        }
    }

    private static bool PickFolder(string title, string startDirectory, out string path) {
        path = null;
        int comResult = OleInitialize(IntPtr.Zero);
        IntPtr display = Marshal.AllocHGlobal(MaxPath * 2);
        try {
            BrowseCallback callback = (window, message, parameter, data) => {
                if (message == BrowseInitialized && !string.IsNullOrEmpty(startDirectory)) {
                    SendMessageW(window, BrowseSetSelection, (IntPtr)1, startDirectory);
                }

                return 0;
            };

            BrowseInfo info = new BrowseInfo {
                hwndOwner = GetActiveWindow(),
                pszDisplayName = display,
                lpszTitle = title,
                ulFlags = BrowseReturnOnlyFileSystemDirs | BrowseNewDialogStyle | BrowseEditBox,
                lpfn = callback
            };

            IntPtr item = SHBrowseForFolderW(ref info);
            GC.KeepAlive(callback);
            if (item == IntPtr.Zero) {
                return true;
            }

            try {
                StringBuilder result = new StringBuilder(MaxPath);
                if (SHGetPathFromIDListW(item, result)) {
                    path = result.ToString();
                }
            } finally {
                CoTaskMemFree(item);
            }

            return true;
        } finally {
            Marshal.FreeHGlobal(display);
            if (comResult == 0 || comResult == 1) {
                OleUninitialize();
            }
        }
    }

    private static bool PickFile(string title, string startDirectory, string filter, out string path) {
        path = null;
        IntPtr buffer = Marshal.AllocHGlobal(FileBufferChars * 2);
        try {
            Marshal.WriteInt16(buffer, 0);
            OpenFileName dialog = new OpenFileName {
                hwndOwner = GetActiveWindow(),
                lpstrFilter = filter,
                nFilterIndex = 1,
                lpstrFile = buffer,
                nMaxFile = FileBufferChars,
                lpstrInitialDir = startDirectory,
                lpstrTitle = title,
                Flags = OpenFileMustExist | OpenPathMustExist | OpenNoChangeDir | OpenExplorer
            };
            dialog.lStructSize = Marshal.SizeOf(dialog);

            if (GetOpenFileNameW(ref dialog)) {
                path = Marshal.PtrToStringUni(buffer);
                return true;
            }

            // no error code means the player cancelled.
            return CommDlgExtendedError() == 0;
        } finally {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private delegate int BrowseCallback(IntPtr window, uint message, IntPtr parameter, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct BrowseInfo {
        public IntPtr hwndOwner;
        public IntPtr pidlRoot;
        public IntPtr pszDisplayName;
        public string lpszTitle;
        public uint ulFlags;
        public BrowseCallback lpfn;
        public IntPtr lParam;
        public int iImage;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileName {
        public int lStructSize;
        public IntPtr hwndOwner;
        public IntPtr hInstance;
        public string lpstrFilter;
        public string lpstrCustomFilter;
        public int nMaxCustFilter;
        public int nFilterIndex;
        public IntPtr lpstrFile;
        public int nMaxFile;
        public string lpstrFileTitle;
        public int nMaxFileTitle;
        public string lpstrInitialDir;
        public string lpstrTitle;
        public int Flags;
        public short nFileOffset;
        public short nFileExtension;
        public string lpstrDefExt;
        public IntPtr lCustData;
        public IntPtr lpfnHook;
        public string lpTemplateName;
        public IntPtr pvReserved;
        public int dwReserved;
        public int FlagsEx;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHBrowseForFolderW(ref BrowseInfo info);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SHGetPathFromIDListW(IntPtr item, StringBuilder path);

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetOpenFileNameW(ref OpenFileName dialog);

    [DllImport("comdlg32.dll")]
    private static extern int CommDlgExtendedError();

    [DllImport("ole32.dll")]
    private static extern int OleInitialize(IntPtr reserved);

    [DllImport("ole32.dll")]
    private static extern void OleUninitialize();

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr memory);

    [DllImport("user32.dll")]
    private static extern IntPtr GetActiveWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, string lParam);
}
