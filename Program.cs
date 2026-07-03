using System.Diagnostics;
using System.Linq;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace MiniAutoAccept;

internal static class Program
{
    private static async Task Main()
    {
        CreateTrayWindow();

        // 真正干活的循环放后台线程跑，不阻塞托盘消息循环
        _ = Task.Run(RunAutoAcceptLoopAsync);

        RunMessageLoop();
    }

    // ================= 自动接受核心逻辑 =================

    private static async Task RunAutoAcceptLoopAsync()
    {
        var waitAttempts = 0;

        while (true)
        {
            var credentials = FindLcuCredentials();
            if (credentials is null)
            {
                waitAttempts++;
                UpdateTrayTip(waitAttempts >= 10
                    ? "League Auto Accept - 未检测到客户端"
                    : "League Auto Accept - 等待客户端...");
                await Task.Delay(3000);
                continue;
            }

            waitAttempts = 0;
            var (port, password) = credentials.Value;
            UpdateTrayTip($"League Auto Accept - 已连接 (端口 {port})");

            try
            {
                await MonitorReadyCheckAsync(port, password);
            }
            catch
            {
                // 忽略，下面会自动重连
            }

            UpdateTrayTip("League Auto Accept - 客户端已关闭，等待重连...");
            await Task.Delay(2000);
        }
    }

    /// <summary>
    /// 找到 LeagueClientUx 进程，通过 PowerShell(Get-CimInstance) 查询它的命令行参数，
    /// 正则提取端口和密码。国服/国际服通用。
    /// </summary>
    private static (int Port, string Password)? FindLcuCredentials()
    {
        var client = Process.GetProcessesByName("LeagueClientUx").FirstOrDefault();
        if (client is null) return null;

        string commandLine;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "powershell",
                Arguments = "-NoProfile -NonInteractive -Command " +
                    $"\"(Get-CimInstance Win32_Process -Filter \\\"ProcessId={client.Id}\\\").CommandLine\"",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var proc = Process.Start(psi);
            if (proc is null) return null;

            commandLine = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);
        }
        catch
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(commandLine)) return null;

        var portMatch = Regex.Match(commandLine, @"--app-port=""?(\d+)""?");
        var tokenMatch = Regex.Match(commandLine, @"--remoting-auth-token=([a-zA-Z0-9_-]+)");

        if (portMatch.Success && tokenMatch.Success && int.TryParse(portMatch.Groups[1].Value, out var port))
        {
            return (port, tokenMatch.Groups[1].Value);
        }

        return null;
    }

    private static async Task MonitorReadyCheckAsync(int port, string password)
    {
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
        };

        using var client = new HttpClient(handler)
        {
            BaseAddress = new Uri($"https://127.0.0.1:{port}"),
            Timeout = TimeSpan.FromSeconds(5),
        };

        var authBytes = Encoding.UTF8.GetBytes($"riot:{password}");
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Basic", Convert.ToBase64String(authBytes));

        // current-summoner 接口只有登录之后才存在，客户端刚启动、还在登录界面时查询会返回404，
        // 这是正常现象，不代表端口/密码错了，所以这里不算失败，只是提示还没登录，
        // 不影响后面继续监听对局确认（登录之后自然就能查到了）
        try
        {
            var testResp = await client.GetAsync("/lol-summoner/v1/current-summoner");
            if (testResp.IsSuccessStatusCode)
            {
                var summonerJson = await testResp.Content.ReadAsStringAsync();
                using var summonerDoc = JsonDocument.Parse(summonerJson);
                var name = summonerDoc.RootElement.TryGetProperty("displayName", out var nameEl)
                    ? nameEl.GetString()
                    : "未知";
                UpdateTrayTip($"League Auto Accept - {name}");
            }
            else if (testResp.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                UpdateTrayTip("League Auto Accept - 已连接，等待登录...");
            }
            else if (testResp.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                UpdateTrayTip("League Auto Accept - 端口/密码验证失败");
            }
            else
            {
                UpdateTrayTip($"League Auto Accept - 连接异常 (HTTP {(int)testResp.StatusCode})");
            }
        }
        catch
        {
            UpdateTrayTip("League Auto Accept - 验证请求失败");
        }

        var wasAccepted = false;

        while (true)
        {
            HttpResponseMessage response;
            try
            {
                response = await client.GetAsync("/lol-matchmaking/v1/ready-check");
            }
            catch (HttpRequestException)
            {
                return; // 客户端大概率已关闭
            }
            catch (TaskCanceledException)
            {
                await Task.Delay(500);
                continue;
            }

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                wasAccepted = false;
                await Task.Delay(800);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                await Task.Delay(800);
                continue;
            }

            var json = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(json);

            var state = doc.RootElement.TryGetProperty("state", out var stateEl)
                ? stateEl.GetString()
                : null;

            if (state == "InProgress" && !wasAccepted)
            {
                var acceptResp = await client.PostAsync("/lol-matchmaking/v1/ready-check/accept", content: null);
                if (acceptResp.IsSuccessStatusCode)
                {
                    wasAccepted = true;
                }
            }
            else if (state != "InProgress")
            {
                wasAccepted = false;
            }

            await Task.Delay(500);
        }
    }

    // ================= 系统托盘 (纯 Win32 P/Invoke，AOT 兼容，不依赖 WinForms) =================

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_COMMAND = 0x0111;
    private const int WM_RBUTTONUP = 0x0205;
    private const uint WM_TRAYICON = 0x8001; // WM_APP + 1
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const int IDI_APPLICATION = 32512;
    private const uint MF_STRING = 0x0000;
    private const int ID_TRAY_EXIT = 1001;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;
        public uint uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;
        public uint dwInfoFlags;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private delegate IntPtr WndProcDelegate(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClassEx(ref WNDCLASSEX lpwcx);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(uint dwExStyle, string lpClassName, string lpWindowName,
        uint dwStyle, int x, int y, int nWidth, int nHeight, IntPtr hWndParent, IntPtr hMenu, IntPtr hInstance, IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIcon(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y, int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string lpText, string lpCaption, uint uType);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint ExtractIconEx(string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

    private static IntPtr _hwnd;
    private static NOTIFYICONDATA _nid;
    private static readonly WndProcDelegate WndProcHandler = WndProc;

    private static void CreateTrayWindow()
    {
        var hInstance = GetModuleHandle(null);
        const string className = "MiniAutoAcceptTrayWnd";

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WndProcHandler),
            hInstance = hInstance,
            lpszClassName = className,
        };

        if (RegisterClassEx(ref wc) == 0)
        {
            MessageBox(IntPtr.Zero, $"窗口类注册失败，错误码: {Marshal.GetLastWin32Error()}", "MiniAutoAccept", 0);
            return;
        }

        _hwnd = CreateWindowEx(0, className, "MiniAutoAccept", 0, 0, 0, 0, 0,
            IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            MessageBox(IntPtr.Zero, $"创建隐藏窗口失败，错误码: {Marshal.GetLastWin32Error()}", "MiniAutoAccept", 0);
            return;
        }

        var hIcon = GetOwnExeIcon() is { } icon ? icon : LoadIcon(IntPtr.Zero, (IntPtr)IDI_APPLICATION);

        _nid = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = hIcon,
            szTip = "League Auto Accept - 启动中...",
            szInfo = "",
            szInfoTitle = "",
        };

        if (!Shell_NotifyIcon(NIM_ADD, ref _nid))
        {
            MessageBox(IntPtr.Zero, $"添加托盘图标失败，错误码: {Marshal.GetLastWin32Error()}", "MiniAutoAccept", 0);
        }
    }

    /// <summary>
    /// 从自身 exe 文件里提取 csproj 里通过 ApplicationIcon 设置的那个图标，
    /// 用小尺寸版本（16x16，适合托盘显示）。
    /// </summary>
    private static IntPtr? GetOwnExeIcon()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath)) return null;

        var smallIcons = new IntPtr[1];
        var count = ExtractIconEx(exePath, 0, null, smallIcons, 1);

        return count > 0 && smallIcons[0] != IntPtr.Zero ? smallIcons[0] : null;
    }

    private static void RunMessageLoop()
    {
        while (GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    private static IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_TRAYICON:
                if ((int)lParam == WM_RBUTTONUP)
                {
                    ShowTrayMenu();
                }
                return IntPtr.Zero;

            case WM_COMMAND:
                var id = (int)wParam & 0xFFFF;
                if (id == ID_TRAY_EXIT)
                {
                    Shell_NotifyIcon(NIM_DELETE, ref _nid);
                    DestroyWindow(hWnd);
                }
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private static void ShowTrayMenu()
    {
        GetCursorPos(out var pt);
        var hMenu = CreatePopupMenu();
        AppendMenu(hMenu, MF_STRING, (IntPtr)ID_TRAY_EXIT, "退出");
        SetForegroundWindow(_hwnd); // 防止右键菜单点击外部区域后不消失
        TrackPopupMenu(hMenu, 0, pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
    }

    private static void UpdateTrayTip(string text)
    {
        if (_hwnd == IntPtr.Zero) return;
        _nid.szTip = text.Length > 127 ? text[..127] : text;
        Shell_NotifyIcon(NIM_MODIFY, ref _nid);
    }
}
