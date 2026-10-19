using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

internal static class AutoHideCursorHelper
{
    private const uint SPI_SETCURSORS = 0x0057;
    private const uint SPIF_SENDCHANGE = 0x0002;
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    private const uint GA_ROOT = 2;
    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;
    private const int VK_XBUTTON1 = 0x05;
    private const int VK_XBUTTON2 = 0x06;

    private static readonly uint[] CursorIds = new uint[]
    {
        32512, // OCR_NORMAL
        32513, // OCR_IBEAM
        32514, // OCR_WAIT
        32515, // OCR_CROSS
        32516, // OCR_UP
        32642, // OCR_SIZENWSE
        32643, // OCR_SIZENESW
        32644, // OCR_SIZEWE
        32645, // OCR_SIZENS
        32646, // OCR_SIZEALL
        32648, // OCR_NO
        32649, // OCR_HAND
        32650, // OCR_APPSTARTING
        32651, // OCR_HELP
        32671, // OCR_PIN
        32672  // OCR_PERSON
    };

    private static readonly object Gate = new object();
    private static readonly Dictionary<uint, string> PathCache = new Dictionary<uint, string>();
    private static volatile bool _hidden;
    private static volatile bool _running = true;
    private static string _vscodeExe = string.Empty;
    private static int _threshold = 3;
    private static int _pollMs = 20;
    private static int _parentPid;
    private static POINT _hidePos;
    private static long _lastParentCheck;
    private static bool _parentAlive = true;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ICONINFO
    {
        public bool fIcon;
        public int xHotspot;
        public int yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref ICONINFO piconinfo);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int nWidth, int nHeight, uint nPlanes, uint nBitCount, byte[] lpBits);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("user32.dll")]
    private static extern bool DestroyCursor(IntPtr hCursor);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern IntPtr WindowFromPoint(POINT point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, uint dwFlags, StringBuilder lpExeName, ref int lpdwSize);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr hObject);

    private static int Main(string[] args)
    {
        if (args.Length >= 1 && args[0] == "--restore")
        {
            RestoreCursors();
            return 0;
        }

        if (args.Length >= 2 && args[0] == "--watchdog")
        {
            return RunWatchdog(args[1]);
        }

        if (args.Length >= 1 && args[0] == "--selftest")
        {
            RestoreCursors();
            Thread.Sleep(1000);
            HideNow();
            Thread.Sleep(2500);
            RestoreCursors();
            return 0;
        }

        if (args.Length < 2)
        {
            Console.Error.WriteLine("usage: AutoHideCursorHelper.exe <parentPid> <vscodeExeBase64> [threshold] [pollMs]");
            return 2;
        }

        _parentPid = int.Parse(args[0]);
        _vscodeExe = Encoding.UTF8.GetString(Convert.FromBase64String(args[1]));
        if (args.Length >= 3)
        {
            _threshold = Math.Max(1, int.Parse(args[2]));
        }
        if (args.Length >= 4)
        {
            _pollMs = Math.Max(10, int.Parse(args[3]));
        }

        RestoreCursors();

        AppDomain.CurrentDomain.ProcessExit += delegate { RestoreCursors(); };
        AppDomain.CurrentDomain.UnhandledException += delegate { RestoreCursors(); };

        StartWatchdog();
        StartStdinReader();

        while (_running)
        {
            Thread.Sleep(_pollMs);
            if (!_hidden)
            {
                continue;
            }

            long now = Environment.TickCount;
            if (now - _lastParentCheck >= 500)
            {
                _lastParentCheck = now;
                _parentAlive = IsProcessAlive(_parentPid);
            }

            if (!_parentAlive)
            {
                RestoreCursors();
                break;
            }

            if (!CursorIsOverVSCode())
            {
                RestoreCursors();
                continue;
            }

            if (AnyMouseButtonDown())
            {
                RestoreCursors();
                continue;
            }

            POINT current;
            if (!GetCursorPos(out current))
            {
                continue;
            }

            int dx = current.X - _hidePos.X;
            int dy = current.Y - _hidePos.Y;
            if (dx * dx + dy * dy >= _threshold * _threshold)
            {
                RestoreCursors();
            }
        }

        RestoreCursors();
        return 0;
    }

    private static void StartStdinReader()
    {
        Thread thread = new Thread(delegate()
        {
            try
            {
                using (StreamReader reader = new StreamReader(Console.OpenStandardInput(), Encoding.UTF8))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                    {
                        line = line.Trim();
                        if (line.Length == 0)
                        {
                            continue;
                        }

                        if (string.Equals(line, "hide", StringComparison.OrdinalIgnoreCase))
                        {
                            TryHide();
                        }
                        else if (string.Equals(line, "show", StringComparison.OrdinalIgnoreCase))
                        {
                            RestoreCursors();
                        }
                        else if (string.Equals(line, "quit", StringComparison.OrdinalIgnoreCase))
                        {
                            break;
                        }
                    }
                }
            }
            catch (Exception)
            {
            }

            _running = false;
            RestoreCursors();
        });
        thread.IsBackground = true;
        thread.Start();
    }

    private static void TryHide()
    {
        lock (Gate)
        {
            if (_hidden)
            {
                return;
            }

            if (!CursorIsOverVSCode())
            {
                return;
            }

            POINT position;
            if (!GetCursorPos(out position))
            {
                return;
            }

            _hidePos = position;
            HideNow();
        }
    }

    private static void HideNow()
    {
        for (int i = 0; i < CursorIds.Length; i++)
        {
            IntPtr cursor = CreateTransparentCursor();
            if (cursor == IntPtr.Zero)
            {
                continue;
            }

            if (!SetSystemCursor(cursor, CursorIds[i]))
            {
                DestroyCursor(cursor);
            }
        }

        _hidden = true;
    }

    private static void RestoreCursors()
    {
        lock (Gate)
        {
            SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, SPIF_SENDCHANGE);
            _hidden = false;
        }
    }

    private static IntPtr CreateTransparentCursor()
    {
        byte[] andMask = new byte[32 * 32 / 8];
        for (int i = 0; i < andMask.Length; i++)
        {
            andMask[i] = 0xFF;
        }
        byte[] xorMask = new byte[32 * 32 / 8];

        IntPtr hMask = CreateBitmap(32, 32, 1, 1, andMask);
        IntPtr hColor = CreateBitmap(32, 32, 1, 1, xorMask);
        if (hMask == IntPtr.Zero || hColor == IntPtr.Zero)
        {
            if (hMask != IntPtr.Zero)
            {
                DeleteObject(hMask);
            }
            if (hColor != IntPtr.Zero)
            {
                DeleteObject(hColor);
            }
            return IntPtr.Zero;
        }

        ICONINFO info = new ICONINFO();
        info.fIcon = false;
        info.xHotspot = 0;
        info.yHotspot = 0;
        info.hbmMask = hMask;
        info.hbmColor = hColor;

        IntPtr cursor = CreateIconIndirect(ref info);
        DeleteObject(hMask);
        DeleteObject(hColor);
        return cursor;
    }

    private static bool CursorIsOverVSCode()
    {
        IntPtr foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero)
        {
            return false;
        }

        if (!IsVSCodeWindow(foreground))
        {
            return false;
        }

        POINT position;
        if (!GetCursorPos(out position))
        {
            return false;
        }

        IntPtr hit = WindowFromPoint(position);
        if (hit == IntPtr.Zero)
        {
            return false;
        }

        IntPtr root = GetAncestor(hit, GA_ROOT);
        if (root == IntPtr.Zero)
        {
            root = hit;
        }

        return IsVSCodeWindow(root);
    }

    private static bool IsVSCodeWindow(IntPtr hwnd)
    {
        uint pid;
        GetWindowThreadProcessId(hwnd, out pid);
        if (pid == 0)
        {
            return false;
        }

        string path = GetProcessPath(pid);
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return string.Equals(path, _vscodeExe, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetProcessPath(uint pid)
    {
        lock (PathCache)
        {
            string cached;
            if (PathCache.TryGetValue(pid, out cached))
            {
                return cached;
            }
        }

        string path = QueryProcessPath(pid);
        lock (PathCache)
        {
            if (PathCache.Count > 64)
            {
                PathCache.Clear();
            }
            PathCache[pid] = path;
        }
        return path;
    }

    private static string QueryProcessPath(uint pid)
    {
        IntPtr handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            StringBuilder buffer = new StringBuilder(1024);
            int size = buffer.Capacity;
            if (QueryFullProcessImageName(handle, 0, buffer, ref size))
            {
                return buffer.ToString(0, size);
            }
            return null;
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    private static bool AnyMouseButtonDown()
    {
        return (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0
            || (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0
            || (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0
            || (GetAsyncKeyState(VK_XBUTTON1) & 0x8000) != 0
            || (GetAsyncKeyState(VK_XBUTTON2) & 0x8000) != 0;
    }

    private static bool IsProcessAlive(int pid)
    {
        try
        {
            Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static void StartWatchdog()
    {
        try
        {
            Process current = Process.GetCurrentProcess();
            ProcessStartInfo info = new ProcessStartInfo();
            info.FileName = current.MainModule.FileName;
            info.Arguments = "--watchdog " + current.Id.ToString();
            info.UseShellExecute = false;
            info.CreateNoWindow = true;
            info.WindowStyle = ProcessWindowStyle.Hidden;
            Process.Start(info);
        }
        catch (Exception)
        {
        }
    }

    private static int RunWatchdog(string pidText)
    {
        int pid;
        if (!int.TryParse(pidText, out pid))
        {
            return 1;
        }

        try
        {
            Process process = Process.GetProcessById(pid);
            process.WaitForExit();
        }
        catch (Exception)
        {
        }

        RestoreCursors();
        return 0;
    }
}
