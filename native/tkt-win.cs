// tkt-win — Windows 카카오톡 PC 앱을 포커스를 뺏지 않고 읽고 쓰는 최소 헬퍼.
//
// macOS 판(native/tkt-ax.swift)과 같은 CLI 계약을 지킨다. 명령마다 프로세스를 새로 띄우고
// JSON 한 줄을 stdout 으로 뱉은 뒤 exit 한다. 상태를 들고 있지 않다.
//
//   tkt-win check                          카카오톡 실행·로그인 여부를 진단한다
//   tkt-win windows                        열려 있는 채팅창 목록
//   tkt-win read <채팅방> [개수] [내이름]  최근 메시지를 읽는다
//   tkt-win send <채팅방> <메시지>         메시지를 보낸다
//   tkt-win open <채팅방>                  닫혀 있는 채팅방을 연다
//   tkt-win raw <채팅방>                   복사된 대화 원문 (진단용, Windows 전용)
//   tkt-win probe [채팅방]                 UI 트리를 덤프한다 (진단용, Windows 전용)
//
// macOS 와 무엇이 다른가:
//
//   창 식별 — 카카오톡 26.x 는 채팅방을 메인 창과 같은 EVA_Window_Dblclk 로 띄운다.
//   제목이 채팅방 이름이라는 점은 macOS 와 같지만 클래스만으로는 메인 창과 구분되지
//   않으므로, 입력창(RICHEDIT50W)을 품고 있는지까지 봐야 채팅방으로 확정할 수 있다.
//
//   읽기 — 여기가 macOS 와 근본적으로 다르다. 카카오톡 PC 의 대화 목록
//   (EVA_VH_ListControl_Dblclk)은 owner-drawn 이면서 접근성 API 에 아무것도 내놓지
//   않는다. UI Automation 으로 열면 자식이 스크롤바뿐이고 지원하는 패턴이 하나도 없으며,
//   레거시 MSAA 로도 시스템 메뉴와 스크롤바만 나온다. 즉 AX 트리를 훑듯 본문을 꺼낼
//   구멍이 없다. 그래서 읽기는 앱이 직접 만들어주는 복사 결과를 클립보드에서 받아온다
//   (대화 목록에 Ctrl+A → Ctrl+C). 사용자의 클립보드는 반드시 원래대로 되돌린다.
//
//   전송 — AX 의 AXSelectedText 에 대응하는 것이 EM_REPLACESEL 이다. WM_SETTEXT 로
//   값을 통째로 덮으면 카카오톡의 입력 처리를 건너뛰어 Enter 가 먹지 않을 수 있어서,
//   전체 선택 후 EM_REPLACESEL 로 치환한다. Enter 는 PostMessage 로 입력창에 직접
//   꽂는다 — 전역 키 이벤트가 아니라 카카오톡 창을 앞으로 끌어올리지 않는다.
//
//   포커스 — 어떤 명령도 창을 앞으로 올리지 않는다. 포커스는 아예 건드리지 않는다.
//   AttachThreadInput 은 수정자 키(Ctrl) 상태를 공유하는 데만 쓰고 SetFocus 는 부르지
//   않는다 — 그 조합이 바로 창을 강제로 활성화시키는 기법이라, 넣는 순간 읽기 때마다
//   카카오톡이 사용자가 보던 창 위로 튀어나온다.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Automation;

namespace Tkt
{
    // MARK: - Win32 바인딩

    internal static class Win32
    {
        public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll")]
        public static extern bool EnumChildWindows(IntPtr parent, EnumWindowsProc callback, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetClassNameW(IntPtr hWnd, StringBuilder buffer, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextW(IntPtr hWnd, StringBuilder buffer, int size);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        [DllImport("user32.dll")]
        public static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

        [DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int cmd);

        // DPI 를 인식하지 않으면 GetWindowRect 가 배율로 나눈 가짜 좌표를 돌려준다.
        // 그 상태로 UIA 좌표와 섞이면 말풍선 좌우 판정이 통째로 어긋난다.
        [DllImport("user32.dll")]
        public static extern bool SetProcessDPIAware();

        [DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        // 카카오톡이 응답하지 않을 때 헬퍼까지 같이 멈추면 안 된다. 값 조회는 전부
        // 타임아웃이 있는 SendMessageTimeout 으로 한다.
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint msg, IntPtr wParam, StringBuilder lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessageTimeoutW(
            IntPtr hWnd, uint msg, IntPtr wParam, string lParam,
            uint flags, uint timeoutMs, out IntPtr result);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool PostMessageW(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

        // 아래 셋은 카카오톡에 수정자 키(Ctrl)를 먹이는 데 쓴다. 우리 스레드의 입력 큐를
        // 카카오톡 스레드에 붙이면 SetKeyboardState 로 만든 Ctrl 눌림 상태를 카카오톡도
        // 같이 보게 된다. 창을 앞으로 올리는 일은 하지 않는다.
        [DllImport("kernel32.dll")]
        public static extern uint GetCurrentThreadId();

        [DllImport("user32.dll")]
        public static extern bool AttachThreadInput(uint from, uint to, bool attach);

        [DllImport("user32.dll")]
        public static extern bool GetKeyboardState(byte[] state);

        [DllImport("user32.dll")]
        public static extern bool SetKeyboardState(byte[] state);

        // 키 메시지의 lParam 에 실을 스캔코드. 실제 키 입력과 같은 모양을 만들어야
        // 앱이 진짜 키로 받아들인다.
        [DllImport("user32.dll")]
        public static extern uint MapVirtualKeyW(uint code, uint mapType);

        // 클립보드 — 카카오톡이 대화 내용을 내주는 유일한 통로다.
        [DllImport("user32.dll")]
        public static extern bool OpenClipboard(IntPtr owner);

        [DllImport("user32.dll")]
        public static extern bool CloseClipboard();

        [DllImport("user32.dll")]
        public static extern bool EmptyClipboard();

        [DllImport("user32.dll")]
        public static extern IntPtr GetClipboardData(uint format);

        [DllImport("user32.dll")]
        public static extern IntPtr SetClipboardData(uint format, IntPtr data);

        [DllImport("user32.dll")]
        public static extern IntPtr GetOpenClipboardWindow();

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalLock(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern bool GlobalUnlock(IntPtr handle);

        [DllImport("kernel32.dll")]
        public static extern IntPtr GlobalAlloc(uint flags, UIntPtr bytes);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        public const uint WM_GETTEXT = 0x000D;
        public const uint WM_GETTEXTLENGTH = 0x000E;
        public const uint WM_KEYDOWN = 0x0100;
        public const uint WM_KEYUP = 0x0101;
        public const uint WM_CHAR = 0x0102;
        public const uint EM_SETSEL = 0x00B1;
        public const uint EM_REPLACESEL = 0x00C2;

        public const uint WM_SETTEXT = 0x000C;

        public const int VK_RETURN = 0x0D;
        public const int VK_ESCAPE = 0x1B;
        public const int VK_CONTROL = 0x11;
        public const int VK_A = 0x41;
        public const int VK_C = 0x43;

        public const uint SMTO_ABORTIFHUNG = 0x0002;
        public const int SW_SHOW = 5;
        public const int SW_RESTORE = 9;

        public const uint CF_UNICODETEXT = 13;
        public const uint GMEM_MOVEABLE = 0x0002;

        public static string ClassOf(IntPtr hWnd)
        {
            StringBuilder sb = new StringBuilder(256);
            GetClassNameW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static string TitleOf(IntPtr hWnd)
        {
            int length = GetWindowTextLengthW(hWnd);
            if (length <= 0) return "";
            StringBuilder sb = new StringBuilder(length + 2);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            return sb.ToString();
        }

        public static uint ProcessOf(IntPtr hWnd)
        {
            uint pid;
            GetWindowThreadProcessId(hWnd, out pid);
            return pid;
        }

        public static RECT RectOf(IntPtr hWnd)
        {
            RECT rect;
            if (!GetWindowRect(hWnd, out rect)) rect = new RECT();
            return rect;
        }

        /// 컨트롤이 들고 있는 텍스트. 커스텀 드로잉 컨트롤은 빈 문자열을 돌려준다.
        public static string TextOf(IntPtr hWnd)
        {
            IntPtr lengthResult;
            SendMessageTimeoutW(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero,
                SMTO_ABORTIFHUNG, 2000, out lengthResult);
            int length = lengthResult.ToInt32();
            if (length <= 0) return "";

            StringBuilder sb = new StringBuilder(length + 2);
            IntPtr unused;
            SendMessageTimeoutW(hWnd, WM_GETTEXT, new IntPtr(sb.Capacity), sb,
                SMTO_ABORTIFHUNG, 4000, out unused);
            return sb.ToString();
        }

        public static List<IntPtr> TopLevelWindows(ICollection<uint> pids)
        {
            List<IntPtr> found = new List<IntPtr>();
            EnumWindows(delegate(IntPtr hWnd, IntPtr lParam)
            {
                if (pids.Contains(ProcessOf(hWnd))) found.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// 자손 창 전부. EnumChildWindows 는 손자 이하까지 훑는다.
        public static List<IntPtr> Descendants(IntPtr parent)
        {
            List<IntPtr> found = new List<IntPtr>();
            EnumChildWindows(parent, delegate(IntPtr hWnd, IntPtr lParam)
            {
                found.Add(hWnd);
                return true;
            }, IntPtr.Zero);
            return found;
        }

        /// 클래스 이름 비교는 대소문자를 무시한다. 카카오톡은 리치에디트를 RICHEDIT50W 로
        /// 등록하는데 버전과 문서에 따라 표기가 갈려서, 정확히 맞추려다 놓치면 채팅방 창을
        /// 통째로 못 찾는다.
        public static bool ClassIs(IntPtr hWnd, string className)
        {
            return string.Equals(ClassOf(hWnd), className, StringComparison.OrdinalIgnoreCase);
        }

        public static IntPtr FirstDescendantOfClass(IntPtr parent, string className)
        {
            foreach (IntPtr child in Descendants(parent))
            {
                if (ClassIs(child, className)) return child;
            }
            return IntPtr.Zero;
        }

        public static List<IntPtr> DescendantsOfClass(IntPtr parent, string className)
        {
            List<IntPtr> found = new List<IntPtr>();
            foreach (IntPtr child in Descendants(parent))
            {
                if (ClassIs(child, className)) found.Add(child);
            }
            return found;
        }
    }

    // MARK: - JSON

    /// 의존성 없이 한 줄 JSON 을 만든다. macOS 판의 jsonEscape 와 같은 역할이다.
    internal static class Json
    {
        public static string Str(string value)
        {
            StringBuilder sb = new StringBuilder("\"");
            foreach (char c in value)
            {
                switch (c)
                {
                    case '"': sb.Append("\\\""); break;
                    case '\\': sb.Append("\\\\"); break;
                    case '\n': sb.Append("\\n"); break;
                    case '\r': sb.Append("\\r"); break;
                    case '\t': sb.Append("\\t"); break;
                    default:
                        if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4"));
                        else sb.Append(c);
                        break;
                }
            }
            return sb.Append('"').ToString();
        }

        public static string Bool(bool value) { return value ? "true" : "false"; }
    }

    // MARK: - 도메인

    internal class Message
    {
        public string Time = "";
        public string Author;
        public string Body = "";
        public bool Mine;
    }

    internal class ChatWindow
    {
        public IntPtr Handle;
        public string Title;
    }

    /// 클립보드 접근.
    ///
    /// 카카오톡이 대화 내용을 내주는 유일한 통로다. 읽기 한 번마다 사용자의 클립보드를
    /// 잠깐 빌리므로 반드시 원래 내용으로 되돌린다. 되돌릴 수 있는 것은 텍스트뿐이라,
    /// 이미지나 파일이 들어 있었다면 그건 잃는다 — README 에 적어둔 제약이다.
    internal static class Clipboard
    {
        /// 클립보드는 한 번에 한 프로세스만 연다. 다른 앱(클립보드 관리 도구가 특히 자주)이
        /// 잡고 있으면 잠깐 기다렸다 다시 시도한다.
        private static bool Open()
        {
            for (int attempt = 0; attempt < 15; attempt++)
            {
                if (Win32.OpenClipboard(IntPtr.Zero)) return true;
                Thread.Sleep(60);
            }
            return false;
        }

        /// 클립보드를 쥐고 있는 창의 정체. 실패 원인을 사용자에게 알려주는 데 쓴다.
        public static string Holder()
        {
            IntPtr holder = Win32.GetOpenClipboardWindow();
            if (holder == IntPtr.Zero) return "";
            string title = Win32.TitleOf(holder);
            string className = Win32.ClassOf(holder);
            return " (점유 중인 창: class='" + className + "'"
                + (title.Length > 0 ? " title='" + title + "'" : "") + ")";
        }

        /// 읽기 실패는 null, 빈 클립보드는 "" 로 구분한다. 둘을 뭉뚱그리면 복원 단계에서
        /// 원래 내용을 지워버린다.
        public static string ReadText()
        {
            if (!Open()) return null;
            try
            {
                IntPtr handle = Win32.GetClipboardData(Win32.CF_UNICODETEXT);
                if (handle == IntPtr.Zero) return "";
                IntPtr pointer = Win32.GlobalLock(handle);
                if (pointer == IntPtr.Zero) return "";
                try { return Marshal.PtrToStringUni(pointer) ?? ""; }
                finally { Win32.GlobalUnlock(handle); }
            }
            finally { Win32.CloseClipboard(); }
        }

        /// null 을 주면 클립보드를 비운다.
        public static bool WriteText(string text)
        {
            if (!Open()) return false;
            try
            {
                Win32.EmptyClipboard();
                if (text == null || text.Length == 0) return true;

                int bytes = (text.Length + 1) * 2;
                IntPtr block = Win32.GlobalAlloc(Win32.GMEM_MOVEABLE, new UIntPtr((uint)bytes));
                if (block == IntPtr.Zero) return false;

                IntPtr pointer = Win32.GlobalLock(block);
                if (pointer == IntPtr.Zero) return false;
                try
                {
                    Marshal.Copy(text.ToCharArray(), 0, pointer, text.Length);
                    Marshal.WriteInt16(pointer, text.Length * 2, 0);
                }
                finally { Win32.GlobalUnlock(block); }

                // SetClipboardData 가 성공하면 메모리 소유권이 클립보드로 넘어간다.
                return Win32.SetClipboardData(Win32.CF_UNICODETEXT, block) != IntPtr.Zero;
            }
            finally { Win32.CloseClipboard(); }
        }
    }

    internal static class Program
    {
        /// 카카오톡 프로세스 이름. 설치 경로와 무관하게 이 이름으로 찾는다.
        private const string ProcessName = "KakaoTalk";

        /// 채팅방 창의 클래스 후보.
        ///
        /// 26.x 는 채팅방을 메인 창과 같은 EVA_Window_Dblclk 로 띄운다. 예전 버전은 표준
        /// 다이얼로그(#32770)를 썼으므로 둘 다 받아둔다. 클래스만으로는 메인 창과 구분되지
        /// 않으니 입력창이 있는지까지 봐야 한다.
        private static readonly string[] ChatWindowClasses = { "EVA_Window_Dblclk", "#32770" };

        /// 채팅방 입력창의 클래스. 이 컨트롤이 있어야 채팅방 창으로 인정한다.
        /// 카카오톡은 대문자로 등록하지만(RICHEDIT50W) 버전에 따라 표기가 달라 대소문자를 무시한다.
        private const string InputClass = "RICHEDIT50W";

        /// 대화 목록·채팅 목록에 공통으로 쓰이는 커스텀 리스트 컨트롤.
        private const string ListClass = "EVA_VH_ListControl_Dblclk";

        /// 메인 창(채팅 목록)의 클래스.
        private const string MainWindowClass = "EVA_Window_Dblclk";

        /// 메인 창 제목. 카카오톡 UI 언어를 탄다.
        private static readonly string[] MainWindowTitles = { "카카오톡", "KakaoTalk" };

        /// 헬퍼가 "채팅창이 안 열려 있다"고 알려줄 때의 표식. kakao.ts 의 WINDOW_CLOSED 와
        /// 짝을 이룬다. 다른 오류 문구에 이 말이 섞이면 닫힌 창으로 오해해 open 이 돌고
        /// 엉뚱한 방이 열린다.
        private const string WindowClosedMark = "열려 있지 않습니다";

        private static uint[] _kakaoPids;

        [STAThread]
        private static int Main(string[] args)
        {
            try { Win32.SetProcessDPIAware(); }
            catch { /* 이미 인식 상태로 떠 있으면 실패한다. 무시해도 된다. */ }

            // Node 가 utf8 로 읽는다. 콘솔 코드페이지에 맡기면 한글이 깨진다.
            StreamWriter stdout = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false));
            stdout.AutoFlush = true;
            Console.SetOut(stdout);
            StreamWriter stderr = new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false));
            stderr.AutoFlush = true;
            Console.SetError(stderr);

            const string usage =
                "usage: tkt-win send <chat> <message> | read <chat> [limit] [myName] | windows | open <chat> | check | raw <chat> | probe [chat]";

            if (args.Length < 1) return Fail(usage);
            string command = args[0];

            try
            {
                // check 는 "무엇이 준비되지 않았는지" 를 알아내는 명령이라 준비된 상태를
                // 전제하는 아래 검사들에 걸리면 안 된다. 가장 먼저 처리한다.
                if (command == "check") return RunCheck();

                if (KakaoPids().Length == 0) return Fail("카카오톡이 실행 중이 아닙니다");

                if (command == "windows") return RunWindows();
                if (command == "probe") return RunProbe(args.Length >= 2 ? args[1] : null);

                if (args.Length < 2) return Fail(usage);
                string chat = args[1];

                if (command == "read")
                {
                    int limit = 30;
                    if (args.Length >= 3 && !int.TryParse(args[2], out limit)) limit = 30;
                    if (limit <= 0) limit = 30;
                    // 카카오톡이 복사해주는 줄에는 내 메시지도 내 이름으로 적힌다. 내 이름을
                    // 알아야 "내가 보낸 것" 을 가를 수 있어서 호출자가 알려준다.
                    string myName = args.Length >= 4 ? args[3] : null;
                    return RunRead(chat, limit, myName);
                }
                if (command == "raw") return RunRaw(chat);
                if (command == "send")
                {
                    if (args.Length < 3) return Fail("usage: tkt-win send <chat> <message>");
                    return RunSend(chat, args[2]);
                }
                if (command == "open") return RunOpen(chat);

                return Fail("알 수 없는 명령: " + command);
            }
            catch (Exception error)
            {
                return Fail(error.Message);
            }
        }

        private static int Fail(string message)
        {
            Console.Error.WriteLine(message);
            Console.Error.Flush();
            return 1;
        }

        // MARK: - 창 찾기

        private static uint[] KakaoPids()
        {
            if (_kakaoPids != null) return _kakaoPids;
            List<uint> pids = new List<uint>();
            foreach (Process process in Process.GetProcessesByName(ProcessName))
            {
                pids.Add((uint)process.Id);
            }
            _kakaoPids = pids.ToArray();
            return _kakaoPids;
        }

        /// 지금 열려 있는 채팅방 창들.
        ///
        /// 카카오톡은 설정·프로필 같은 다른 다이얼로그도 #32770 으로 띄우므로 클래스만으로는
        /// 부족하다. 입력창(RichEdit50W)을 품고 있는 창만 채팅방으로 인정한다.
        ///
        /// 최소화한 창도 포함한다. 창은 살아 있고 읽기·전송이 모두 되는데 여기서 걸러내면
        /// 상위 계층이 "닫힌 방" 으로 오해해 open 을 태우고, 사용자가 내려둔 창을 도로
        /// 끄집어낸다. macOS 판이 최소화·숨김 상태의 AXDialog 를 인정하는 것과 같은 이유다.
        private static List<ChatWindow> ChatWindows()
        {
            HashSet<uint> pids = new HashSet<uint>(KakaoPids());
            List<ChatWindow> found = new List<ChatWindow>();

            foreach (IntPtr hWnd in Win32.TopLevelWindows(pids))
            {
                bool classMatches = false;
                foreach (string candidate in ChatWindowClasses)
                {
                    if (Win32.ClassIs(hWnd, candidate)) { classMatches = true; break; }
                }
                if (!classMatches) continue;

                string title = Win32.TitleOf(hWnd);
                if (title.Length == 0) continue;
                if (Array.IndexOf(MainWindowTitles, title) >= 0) continue;
                if (Win32.FirstDescendantOfClass(hWnd, InputClass) == IntPtr.Zero) continue;

                ChatWindow window = new ChatWindow();
                window.Handle = hWnd;
                window.Title = title;
                found.Add(window);
            }
            return found;
        }

        /// 제목과 클래스로만 찾은 메인 창. 로그인 여부·잠금 여부를 가리기 전 단계다.
        private static IntPtr MainWindowAny()
        {
            HashSet<uint> pids = new HashSet<uint>(KakaoPids());
            foreach (IntPtr hWnd in Win32.TopLevelWindows(pids))
            {
                if (!Win32.ClassIs(hWnd, MainWindowClass)) continue;
                if (Array.IndexOf(MainWindowTitles, Win32.TitleOf(hWnd)) < 0) continue;
                return hWnd;
            }
            return IntPtr.Zero;
        }

        /// 메인 창(채팅 목록). 트레이로 내려가 숨어 있어도 핸들은 살아 있다.
        /// 로그인 전에는 리스트 컨트롤이 없어서, 그걸로 로그인 여부까지 가른다.
        private static IntPtr MainWindow()
        {
            IntPtr main = MainWindowAny();
            if (main == IntPtr.Zero) return IntPtr.Zero;
            if (Win32.FirstDescendantOfClass(main, ListClass) == IntPtr.Zero) return IntPtr.Zero;
            return main;
        }

        /// 카카오톡이 화면 잠금(잠금모드) 상태인지.
        ///
        /// 잠금이 걸리면 열어둔 채팅방 창이 모두 닫히고 메인 창이 잠금 화면으로 덮인다.
        /// 그러면 읽기도 전송도 할 수 없는데, "채팅창이 열려 있지 않습니다" 만 보이면
        /// 창을 닫은 적 없는 사용자는 영문을 모른다. 원인을 짚어주려고 따로 본다.
        ///
        /// 카카오톡은 사용자가 일정 시간 앱을 만지지 않으면 잠그는데, tkt 로 보내는 메시지는
        /// 합성된 것이라 "사용 중" 으로 쳐주지 않는다. 그래서 tkt 를 쓰는 동안에도 잠긴다.
        private static bool IsLocked()
        {
            IntPtr main = MainWindowAny();
            if (main == IntPtr.Zero) return false;

            foreach (IntPtr child in Win32.Descendants(main))
            {
                if (!Win32.TextOf(child).StartsWith("LockModeView")) continue;
                return Win32.IsWindowVisible(child);
            }
            return false;
        }

        private enum MatchKind { Found, NotOpen, Ambiguous }

        private class Match
        {
            public MatchKind Kind;
            public ChatWindow Window;
            public List<string> Candidates;
        }

        /// 제목으로 채팅창을 고른다.
        ///
        /// 정확히 일치하는 창을 먼저 보고, 없을 때만 부분 일치로 넓힌다. 부분 일치가 여럿이면
        /// 아무것도 고르지 않는다 — "김학재" 는 "김학재 가족방" 에도 걸리기 때문에, 그대로 두면
        /// 엉뚱한 방으로 메시지가 나간다. 읽기는 화면만 이상해지고 말지만 전송은 되돌릴 수 없다.
        private static Match MatchChatWindow(string name)
        {
            List<ChatWindow> candidates = ChatWindows();
            Match match = new Match();

            foreach (ChatWindow window in candidates)
            {
                if (window.Title == name)
                {
                    match.Kind = MatchKind.Found;
                    match.Window = window;
                    return match;
                }
            }

            List<ChatWindow> partial = new List<ChatWindow>();
            foreach (ChatWindow window in candidates)
            {
                if (window.Title.Contains(name)) partial.Add(window);
            }

            if (partial.Count == 1)
            {
                match.Kind = MatchKind.Found;
                match.Window = partial[0];
                return match;
            }
            if (partial.Count > 1)
            {
                match.Kind = MatchKind.Ambiguous;
                match.Candidates = new List<string>();
                foreach (ChatWindow window in partial) match.Candidates.Add(window.Title);
                return match;
            }

            match.Kind = MatchKind.NotOpen;
            return match;
        }

        private static string AmbiguousMessage(string name, List<string> matched)
        {
            return "'" + name + "' 와(과) 이름이 겹치는 채팅창이 여럿입니다: ["
                + string.Join(", ", matched.ToArray()) + "]. "
                + "어느 방인지 특정할 수 없어 아무 것도 하지 않았습니다. 채팅방 이름을 정확히 지정하세요.";
        }

        private static string NotOpenMessage(string name)
        {
            List<string> titles = new List<string>();
            foreach (ChatWindow window in ChatWindows()) titles.Add(window.Title);

            string message = "'" + name + "' 채팅창이 " + WindowClosedMark
                + ". 열린 창: [" + string.Join(", ", titles.ToArray()) + "]";

            // 잠금이 걸리면 채팅방 창이 통째로 닫힌다. 창 목록만 보여주면 사용자는 자기가
            // 닫지도 않은 창이 왜 사라졌는지 알 수 없다.
            if (IsLocked())
            {
                message += " — 카카오톡이 잠금 상태입니다. 잠금을 풀고 채팅방을 다시 열어주세요. "
                    + "계속 잠긴다면 카카오톡 설정에서 자동 잠금을 꺼야 합니다.";
            }
            return message;
        }

        /// 채팅창을 찾는다. 못 찾으면 이유를 stderr 로 밝히고 null 을 돌려준다.
        private static ChatWindow FindChatWindow(string name, out int exitCode)
        {
            Match match = MatchChatWindow(name);
            if (match.Kind == MatchKind.Found)
            {
                exitCode = 0;
                return match.Window;
            }
            if (match.Kind == MatchKind.Ambiguous)
            {
                // 이 문구에 WindowClosedMark 가 들어가면 안 된다. 들어가면 창이 닫힌
                // 것으로 오해해 open 을 시도하고, 결국 아무 방이나 열어버린다.
                exitCode = Fail(AmbiguousMessage(name, match.Candidates));
                return null;
            }
            exitCode = Fail(NotOpenMessage(name));
            return null;
        }

        // MARK: - check / windows

        private static int RunCheck()
        {
            // Windows 에는 macOS 의 손쉬운 사용 권한 같은 관문이 없다. 대신 카카오톡이
            // 관리자 권한으로 떠 있으면 일반 권한 프로세스는 그 창에 메시지를 보낼 수 없다.
            // trusted 는 그 "조작 가능 여부" 를 뜻한다.
            bool running = KakaoPids().Length > 0;
            bool trusted = true;

            if (running)
            {
                try
                {
                    Process[] processes = Process.GetProcessesByName(ProcessName);
                    if (processes.Length > 0)
                    {
                        // 권한이 모자라면 여기서 Win32Exception 이 난다.
                        string unused = processes[0].MainModule.FileName;
                    }
                }
                catch
                {
                    trusted = false;
                }
            }

            Console.WriteLine("{\"trusted\":" + Json.Bool(trusted)
                + ",\"kakaoRunning\":" + Json.Bool(running)
                + ",\"loggedIn\":" + Json.Bool(running && MainWindow() != IntPtr.Zero)
                + ",\"locked\":" + Json.Bool(running && IsLocked()) + "}");
            return 0;
        }

        private static int RunWindows()
        {
            List<string> items = new List<string>();
            foreach (ChatWindow window in ChatWindows()) items.Add(Json.Str(window.Title));
            Console.WriteLine("{\"windows\":[" + string.Join(",", items.ToArray()) + "]}");
            return 0;
        }

        // MARK: - UIA 유틸

        /// UIA 속성 접근은 요소가 사라지는 순간 예외를 던진다. 읽기 한 번에 수백 개를
        /// 훑으므로 조각 하나가 사라졌다고 전체가 죽으면 안 된다.
        private static string NameOf(AutomationElement element)
        {
            try { return element.Current.Name ?? ""; }
            catch { return ""; }
        }

        private static string ClassOf(AutomationElement element)
        {
            try { return element.Current.ClassName ?? ""; }
            catch { return ""; }
        }

        private static string TypeOf(AutomationElement element)
        {
            try { return element.Current.ControlType.ProgrammaticName; }
            catch { return "?"; }
        }

        private static bool BoundsOf(AutomationElement element, out Rect bounds)
        {
            bounds = Rect.Empty;
            try
            {
                Rect rect = element.Current.BoundingRectangle;
                if (rect.IsEmpty || rect.Width <= 0) return false;
                bounds = rect;
                return true;
            }
            catch { return false; }
        }

        private static List<AutomationElement> ChildrenOf(AutomationElement element)
        {
            List<AutomationElement> found = new List<AutomationElement>();
            try
            {
                TreeWalker walker = TreeWalker.RawViewWalker;
                AutomationElement child = walker.GetFirstChild(element);
                while (child != null)
                {
                    found.Add(child);
                    child = walker.GetNextSibling(child);
                }
            }
            catch { }
            return found;
        }

        // MARK: - read

        /// 대화 목록 컨트롤. 채팅방 창 안에 리스트가 여럿이면 가장 넓은 것을 고른다
        /// (검색 결과 목록 같은 보조 리스트가 같은 클래스로 뜬다).
        private static IntPtr TranscriptControl(ChatWindow window)
        {
            List<IntPtr> lists = Win32.DescendantsOfClass(window.Handle, ListClass);
            IntPtr best = IntPtr.Zero;
            long bestArea = -1;

            foreach (IntPtr list in lists)
            {
                Win32.RECT rect = Win32.RectOf(list);
                long area = (long)Math.Max(0, rect.Right - rect.Left) * Math.Max(0, rect.Bottom - rect.Top);
                if (area > bestArea) { bestArea = area; best = list; }
            }
            return best;
        }

        /// 카카오톡 안쪽 포커스만 대화 목록으로 옮기고 Ctrl+조합키를 먹인다.
        ///
        /// 창을 앞으로 올리지 않는 것이 핵심이다. SetForegroundWindow 를 쓰면 사용자가
        /// 보던 창 위로 카카오톡이 튀어나온다 — 이 도구를 만든 이유가 사라진다.
        /// 대신 우리 스레드의 입력 큐를 카카오톡 스레드에 붙여(AttachThreadInput) 앱 안쪽
        /// 포커스를 옮기고, 키 눌림 상태를 공유한 채 키 메시지를 직접 보낸다.
        ///
        /// 커스텀 컨트롤은 수정자 키를 키보드 상태로 확인한다. 키 메시지에는 그 상태가
        /// 실려 있지 않으므로, SetKeyboardState 로 Ctrl 을 눌린 것처럼 만들어야 Ctrl+A 가
        /// Ctrl+A 로 해석된다.
        private static bool SendKeysTo(IntPtr control, int[] keys, bool withControl)
        {
            uint pid;
            uint targetThread = Win32.GetWindowThreadProcessId(control, out pid);
            uint myThread = Win32.GetCurrentThreadId();
            if (targetThread == 0) return false;

            bool attached = Win32.AttachThreadInput(myThread, targetThread, true);
            byte[] saved = new byte[256];
            bool savedOk = Win32.GetKeyboardState(saved);

            try
            {
                // SetFocus 는 부르지 않는다.
                //
                // AttachThreadInput 으로 입력 큐를 붙인 상태에서 SetFocus 를 하면 그 창이
                // 활성화되면서 화면 맨 앞으로 올라온다 (포커스를 강제로 뺏는 고전적인 기법이
                // 바로 이 조합이다). 실측해 보면 SetFocus 없이도 복사는 똑같이 성공하고,
                // 넣는 순간 읽기 때마다 카카오톡 창이 사용자가 보던 창 위로 튀어나온다.
                //
                // 키 메시지는 어차피 컨트롤 핸들로 직접 보내므로 포커스가 필요 없다.
                byte[] state = new byte[256];
                Win32.GetKeyboardState(state);
                state[Win32.VK_CONTROL] = withControl ? (byte)0x80 : (byte)0x00;
                Win32.SetKeyboardState(state);

                // Ctrl 눌림을 메시지로도 알린다. 컨트롤이 상태를 자체적으로 추적할 수 있다.
                if (withControl) SendKeyEvent(control, Win32.VK_CONTROL, false);

                foreach (int key in keys)
                {
                    SendKeyEvent(control, key, false);
                    SendKeyEvent(control, key, true);
                    // 카카오톡이 전체 선택을 마치기 전에 복사가 들어가면 빈 값이 복사된다.
                    Thread.Sleep(250);
                }

                if (withControl) SendKeyEvent(control, Win32.VK_CONTROL, true);
                return true;
            }
            finally
            {
                // Ctrl 을 눌린 상태로 두고 나가면 사용자의 다음 키 입력이 전부 단축키가 된다.
                // 무슨 일이 있어도 되돌린다.
                if (savedOk) Win32.SetKeyboardState(saved);
                else
                {
                    byte[] cleared = new byte[256];
                    Win32.GetKeyboardState(cleared);
                    cleared[Win32.VK_CONTROL] = 0;
                    Win32.SetKeyboardState(cleared);
                }
                if (attached) Win32.AttachThreadInput(myThread, targetThread, false);
            }
        }

        /// 키 이벤트 하나(누름 또는 뗌)를 컨트롤에 보낸다.
        ///
        /// SendMessage 와 PostMessage 를 둘 다 쓴다. SendMessage 만으로는 카카오톡의 커스텀
        /// 리스트 컨트롤이 반응하지 않는다 — 자체 메시지 루프에서 스레드 큐를 직접 훑는
        /// 구조라, 큐에 실제로 들어가는 PostMessage 가 있어야 키를 본다. 반대로 PostMessage
        /// 만 남기면 처리 시점이 어긋나 복사가 빈 채로 끝나는 일이 있었다.
        ///
        /// 키가 두 번 처리될 수 있지만 여기서 쓰는 Ctrl+A·Ctrl+C·Esc 는 모두 여러 번 눌러도
        /// 결과가 같아서 문제되지 않는다.
        private static void SendKeyEvent(IntPtr control, int key, bool up)
        {
            // 실제 키 입력이 만들어내는 것과 같은 모양의 lParam. 반복 횟수 1 에 스캔코드를
            // 얹고, 뗄 때는 이전 상태·전이 비트를 세운다.
            uint scan = Win32.MapVirtualKeyW((uint)key, 0);
            uint bits = 1u | (scan << 16);
            if (up) bits |= 0xC0000000u;
            IntPtr lParam = new IntPtr(unchecked((int)bits));

            uint message = up ? Win32.WM_KEYUP : Win32.WM_KEYDOWN;
            IntPtr unused;
            Win32.SendMessageTimeoutW(control, message, new IntPtr(key), lParam,
                Win32.SMTO_ABORTIFHUNG, 3000, out unused);
            Win32.PostMessageW(control, message, new IntPtr(key), lParam);
        }

        /// 대화 목록을 통째로 복사해 문자열로 돌려준다.
        ///
        /// 카카오톡은 대화 내용을 접근성 트리(UI Automation·MSAA)에 전혀 내놓지 않는다.
        /// 컨트롤이 지원하는 UIA 패턴이 하나도 없어서 텍스트를 꺼낼 구멍이 없다. 남은 길은
        /// 앱이 직접 만들어주는 복사 결과를 클립보드에서 받아오는 것뿐이다.
        ///
        /// 사용자의 클립보드를 잠깐 빌리는 것이므로 무슨 일이 있어도 되돌려 놓는다.
        private static string CopyTranscript(IntPtr list, out string failure)
        {
            failure = null;

            string backup = Clipboard.ReadText();
            if (backup == null)
            {
                failure = "클립보드를 열지 못했습니다" + Clipboard.Holder()
                    + ". 카카오톡은 대화 내용을 접근성 API 로 내주지 않아서 복사 말고는 읽을 방법이 "
                    + "없습니다. 클립보드를 붙잡고 있는 프로그램(클립보드 관리 도구 등)을 잠시 꺼주세요.";
                return null;
            }

            try
            {
                // 복사가 실패했는데 이전 내용이 남아 있으면 그걸 대화로 착각한다. 먼저 비운다.
                Clipboard.WriteText(null);
                Thread.Sleep(60);

                if (!SendKeysTo(list, new int[] { Win32.VK_A, Win32.VK_C }, true))
                {
                    failure = "대화 목록에 키 입력을 전달하지 못했습니다";
                    return null;
                }

                // 복사는 비동기로 끝날 수 있다. 내용이 들어올 때까지 잠깐 기다린다.
                string copied = "";
                for (int attempt = 0; attempt < 12; attempt++)
                {
                    Thread.Sleep(100);
                    copied = Clipboard.ReadText() ?? "";
                    if (copied.Length > 0) break;
                }

                if (copied.Length == 0)
                {
                    failure = "대화 목록을 복사하지 못했습니다. 카카오톡 대화창에서 Ctrl+A → Ctrl+C 가 "
                        + "동작하는지 직접 확인해 보세요. 동작하지 않는 버전이라면 이 방식으로는 읽을 수 없습니다.";
                    return null;
                }
                return copied;
            }
            finally
            {
                // 선택 상태는 그냥 둔다.
                //
                // 예전에는 Ctrl+A 로 물든 선택을 지우려고 Esc 를 보냈는데, 카카오톡에서 Esc 는
                // 채팅방 창을 닫는 키다. 읽기 한 번에 창이 사라지고, 그 뒤로는 "창이 없다 →
                // open 시도 → 실패" 가 폴링마다 반복됐다. 선택 표시가 남는 편이 훨씬 낫다.
                Clipboard.WriteText(backup);
            }
        }

        /// "오후 3:22" / "3:22 PM" 을 "15:22" 로 맞춘다.
        ///
        /// 화면에서 시각 칸은 여섯 글자 폭이라 "오후 3:22" 를 그대로 넣으면 밀린다.
        /// macOS 판이 돌려주는 24시간 표기와도 이걸로 모양이 맞는다.
        private static string NormalizeTime(string raw)
        {
            string text = raw.Trim();
            if (text.Length == 0) return "";

            bool afternoon = false;
            bool morning = false;

            foreach (string marker in new string[] { "오후", "PM", "pm" })
            {
                if (text.StartsWith(marker) || text.EndsWith(marker)) { afternoon = true; break; }
            }
            foreach (string marker in new string[] { "오전", "AM", "am" })
            {
                if (text.StartsWith(marker) || text.EndsWith(marker)) { morning = true; break; }
            }

            foreach (string marker in new string[] { "오전", "오후", "AM", "PM", "am", "pm" })
            {
                text = text.Replace(marker, "");
            }
            text = text.Trim();

            string[] parts = text.Split(':');
            if (parts.Length != 2) return raw.Trim();

            int hour, minute;
            if (!int.TryParse(parts[0].Trim(), out hour)) return raw.Trim();
            if (!int.TryParse(parts[1].Trim(), out minute)) return raw.Trim();

            if (afternoon && hour < 12) hour += 12;
            if (morning && hour == 12) hour = 0;

            return hour.ToString("00") + ":" + minute.ToString("00");
        }

        /// 복사된 대화를 메시지 목록으로 자른다.
        ///
        /// 카카오톡이 만들어주는 줄은 "[보낸 사람] [오후 3:22] 본문" 꼴이다. 이 틀에 맞지
        /// 않는 줄은 바로 앞 메시지의 이어지는 줄(여러 줄 메시지)로 붙인다. 첫 메시지가
        /// 나오기 전의 그런 줄은 날짜 구분선 같은 것이므로 버린다.
        private static List<Message> ParseTranscript(string copied, string myName)
        {
            List<Message> messages = new List<Message>();
            string[] lines = copied.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');

            foreach (string line in lines)
            {
                string author, time, body;
                if (SplitTranscriptLine(line, out author, out time, out body))
                {
                    Message message = new Message();
                    message.Time = NormalizeTime(time);
                    message.Author = author;
                    message.Body = body;
                    message.Mine = myName != null && myName.Length > 0 && author == myName;
                    messages.Add(message);
                }
                else if (IsDateSeparator(line))
                {
                    // 날짜 구분선. 대화가 아니므로 버린다. 이걸 걸러내지 않으면 앞 메시지의
                    // 본문에 "2026년 7월 9일 목요일" 이 따라붙는다.
                    continue;
                }
                else if (messages.Count > 0)
                {
                    // 여러 줄 메시지의 이어지는 줄.
                    messages[messages.Count - 1].Body += "\n" + line;
                }
            }

            foreach (Message message in messages) message.Body = message.Body.TrimEnd();
            return messages;
        }

        /// 대화 사이에 끼는 날짜 구분선인지. "2026년 7월 9일 목요일" 또는 영어 UI 의
        /// "Thursday, July 9, 2026" 꼴이다.
        ///
        /// 여러 줄 메시지의 둘째 줄과 겉모습이 같아서 (둘 다 대괄호로 시작하지 않는다)
        /// 내용으로 가려낼 수밖에 없다. 요일 이름이 들어 있고 숫자로 시작하거나 끝나는
        /// 짧은 줄만 구분선으로 본다.
        private static bool IsDateSeparator(string line)
        {
            string text = line.Trim();
            if (text.Length == 0 || text.Length > 40) return false;

            string[] weekdays = {
                "월요일", "화요일", "수요일", "목요일", "금요일", "토요일", "일요일",
                "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday", "Sunday",
            };

            bool hasWeekday = false;
            foreach (string weekday in weekdays)
            {
                if (text.IndexOf(weekday, StringComparison.OrdinalIgnoreCase) >= 0) { hasWeekday = true; break; }
            }
            if (!hasWeekday) return false;

            // 요일 이름만으로는 부족하다 ("목요일에 보자" 같은 메시지가 있다). 날짜 숫자가
            // 함께 있어야 구분선으로 친다.
            int digits = 0;
            foreach (char c in text)
            {
                if (char.IsDigit(c)) digits++;
            }
            return digits >= 5;
        }

        /// "[이름] [시각] 본문" 한 줄을 쪼갠다. 틀에 맞지 않으면 false.
        private static bool SplitTranscriptLine(string line, out string author, out string time, out string body)
        {
            author = null; time = null; body = null;
            if (line.Length == 0 || line[0] != '[') return false;

            int authorEnd = line.IndexOf(']');
            if (authorEnd < 0) return false;

            int timeStart = authorEnd + 1;
            while (timeStart < line.Length && line[timeStart] == ' ') timeStart++;
            if (timeStart >= line.Length || line[timeStart] != '[') return false;

            int timeEnd = line.IndexOf(']', timeStart);
            if (timeEnd < 0) return false;

            author = line.Substring(1, authorEnd - 1);
            time = line.Substring(timeStart + 1, timeEnd - timeStart - 1);
            body = line.Substring(timeEnd + 1).TrimStart();

            // 시각 자리에 시각이 아닌 것이 들어 있으면 대화 줄이 아니다.
            if (time.IndexOf(':') < 0) return false;
            return true;
        }

        private static int RunRead(string chat, int limit, string myName)
        {
            int exitCode;
            ChatWindow window = FindChatWindow(chat, out exitCode);
            if (window == null) return exitCode;

            IntPtr listHandle = TranscriptControl(window);
            if (listHandle == IntPtr.Zero)
            {
                return Fail("'" + chat + "' 채팅창에서 대화 영역을 찾지 못했습니다");
            }

            string failure;
            string copied = CopyTranscript(listHandle, out failure);
            if (copied == null) return Fail(failure);

            List<Message> messages = ParseTranscript(copied, myName);

            int skip = Math.Max(0, messages.Count - limit);
            List<string> items = new List<string>();
            for (int i = skip; i < messages.Count; i++)
            {
                Message message = messages[i];
                string authorField = message.Author == null ? "null" : Json.Str(message.Author);
                items.Add("{\"time\":" + Json.Str(message.Time)
                    + ",\"author\":" + authorField
                    + ",\"body\":" + Json.Str(message.Body)
                    + ",\"mine\":" + Json.Bool(message.Mine) + "}");
            }

            Console.WriteLine("{\"chat\":" + Json.Str(chat)
                + ",\"messages\":[" + string.Join(",", items.ToArray()) + "]}");
            return 0;
        }

        /// 복사 결과를 가공 없이 그대로 보여준다.
        ///
        /// 카카오톡 버전이 복사 형식을 바꾸면 파서가 조용히 빈 목록을 돌려주게 된다.
        /// 그때 무엇이 실제로 복사됐는지 확인하는 통로다.
        private static int RunRaw(string chat)
        {
            int exitCode;
            ChatWindow window = FindChatWindow(chat, out exitCode);
            if (window == null) return exitCode;

            IntPtr listHandle = TranscriptControl(window);
            if (listHandle == IntPtr.Zero) return Fail("대화 영역을 찾지 못했습니다");

            string failure;
            string copied = CopyTranscript(listHandle, out failure);
            if (copied == null) return Fail(failure);

            Console.WriteLine("복사된 원문 " + copied.Length + "자:");
            Console.WriteLine(copied);
            return 0;
        }


        // MARK: - send

        /// 입력창이 들고 있는 값. RichEdit 은 줄바꿈을 \r 로 저장하므로 비교 전에 맞춰준다.
        private static string InputText(IntPtr input)
        {
            return Win32.TextOf(input).Replace("\r\n", "\n").Replace("\r", "\n");
        }

        /// 키 이벤트 없이 보낸다.
        ///
        /// 입력창의 기존 내용을 통째로 선택하고 EM_REPLACESEL 로 치환한다. WM_SETTEXT 로
        /// 값을 덮는 것과 달리 앱의 텍스트 처리를 거치므로 카카오톡이 내용을 인식한다.
        /// Enter 는 PostMessage 로 입력창에 직접 꽂는다 — 전역 키 이벤트가 아니라서
        /// 카카오톡 창이 다른 앱 위로 올라오지 않는다. 이게 이 도구의 존재 이유다.
        ///
        /// 성공 여부는 반환값으로 알 수 없다. 입력창이 비워졌는지로 판정한다.
        private static bool SendWithoutKeyboard(IntPtr input, string message)
        {
            IntPtr unused;

            // 전체 선택 후 치환. wParam 0, lParam -1 이 "처음부터 끝까지" 다.
            Win32.SendMessageTimeoutW(input, Win32.EM_SETSEL, IntPtr.Zero, new IntPtr(-1),
                Win32.SMTO_ABORTIFHUNG, 2000, out unused);
            Win32.SendMessageTimeoutW(input, Win32.EM_REPLACESEL, new IntPtr(1), message,
                Win32.SMTO_ABORTIFHUNG, 4000, out unused);

            Thread.Sleep(120);

            string normalized = message.Replace("\r\n", "\n").Replace("\r", "\n");
            if (InputText(input) != normalized) return false;

            // KEYDOWN 만으로 반응하지 않는 빌드가 있어 WM_CHAR 까지 같이 보낸다.
            Win32.PostMessageW(input, Win32.WM_KEYDOWN, new IntPtr(Win32.VK_RETURN), IntPtr.Zero);
            Win32.PostMessageW(input, Win32.WM_CHAR, new IntPtr(Win32.VK_RETURN), IntPtr.Zero);
            Win32.PostMessageW(input, Win32.WM_KEYUP, new IntPtr(Win32.VK_RETURN), IntPtr.Zero);

            for (int i = 0; i < 20; i++)
            {
                Thread.Sleep(100);
                if (InputText(input).Length == 0) return true;
            }
            return false;
        }

        private static int RunSend(string chat, string message)
        {
            if (message.Length == 0) return Fail("빈 메시지는 보내지 않습니다");

            int exitCode;
            ChatWindow window = FindChatWindow(chat, out exitCode);
            if (window == null) return exitCode;

            IntPtr input = Win32.FirstDescendantOfClass(window.Handle, InputClass);
            if (input == IntPtr.Zero)
            {
                return Fail("'" + chat + "' 채팅창에서 입력창을 찾지 못했습니다");
            }

            if (SendWithoutKeyboard(input, message))
            {
                Console.WriteLine("sent");
                return 0;
            }

            // 여기까지 왔다면 치환이 안 먹었거나 Enter 가 무시된 것이다. 남은 내용을 지워
            // 사용자가 반쯤 입력된 문장을 발견하는 일이 없게 한다.
            IntPtr unused;
            Win32.SendMessageTimeoutW(input, Win32.EM_SETSEL, IntPtr.Zero, new IntPtr(-1),
                Win32.SMTO_ABORTIFHUNG, 2000, out unused);
            Win32.SendMessageTimeoutW(input, Win32.EM_REPLACESEL, new IntPtr(1), "",
                Win32.SMTO_ABORTIFHUNG, 2000, out unused);

            return Fail("메시지를 보내지 못했습니다. 카카오톡 설정에서 「Enter 로 전송」이 꺼져 "
                + "있으면 이 방식으로는 보낼 수 없습니다 — 설정 → 채팅에서 켜주세요.");
        }

        // MARK: - open

        /// 메인 창의 채팅방 검색 입력칸.
        ///
        /// 채팅 목록은 owner-drawn 이라 행을 직접 고를 수 없다. 검색칸은 평범한 Edit 이라
        /// 값을 넣을 수 있어서, 목록을 좁히는 유일한 손잡이다. 메인 창에는 Edit 이 여럿
        /// (로그인·잠금 화면의 잔재까지) 있으므로 화면에 보이고 폭이 있는 것만 고른다.
        ///
        /// 다만 카카오톡은 검색칸을 늘 그려두지 않는다. 사용자가 검색 아이콘을 누르기
        /// 전에는 폭 0 짜리로만 존재해서, 이 함수가 빈손으로 돌아오는 경우가 흔하다.
        /// 그때는 방을 대신 열어줄 방법이 없다 — RunOpen 이 그 사실을 그대로 알린다.
        private static IntPtr SearchField(IntPtr main)
        {
            foreach (IntPtr child in Win32.DescendantsOfClass(main, "Edit"))
            {
                if (!Win32.IsWindowVisible(child)) continue;
                Win32.RECT rect = Win32.RectOf(child);
                if (rect.Right - rect.Left < 80) continue;
                return child;
            }
            return IntPtr.Zero;
        }

        private static void SetControlText(IntPtr control, string text)
        {
            IntPtr unused;
            Win32.SendMessageTimeoutW(control, Win32.WM_SETTEXT, IntPtr.Zero, text,
                Win32.SMTO_ABORTIFHUNG, 3000, out unused);
        }

        /// 닫혀 있는 채팅방을 연다.
        ///
        /// macOS 판은 채팅 목록에서 방 행을 직접 찾아 고르지만, Windows 카카오톡의 목록은
        /// 접근성에 아무것도 내놓지 않아 행을 짚을 수 없다. 그래서 검색칸에 이름을 넣고
        /// Enter 로 첫 결과를 여는 방식을 쓴다.
        ///
        /// 이 명령은 메인 창을 화면에 드러낸다. 방을 여는 동작이라 자연스럽고, macOS 판도
        /// 같은 자리에서 목록 창을 앞으로 올린다.
        ///
        /// 검색 결과를 읽을 수 없으므로 "무엇이 열렸는지" 는 열린 뒤에야 확인할 수 있다.
        /// 요청한 이름의 창이 뜨지 않으면 성공으로 치지 않는다.
        private static int RunOpen(string chat)
        {
            Match match = MatchChatWindow(chat);
            if (match.Kind == MatchKind.Found)
            {
                Console.WriteLine("already open");
                return 0;
            }
            if (match.Kind == MatchKind.Ambiguous)
            {
                return Fail(AmbiguousMessage(chat, match.Candidates));
            }

            IntPtr main = MainWindow();
            if (main == IntPtr.Zero)
            {
                return Fail("카카오톡 메인 창을 찾지 못했습니다. 카카오톡에 로그인했는지 확인하세요.");
            }

            // 메인 창을 억지로 띄우지 않는다.
            //
            // 예전에는 여기서 ShowWindow + SetForegroundWindow 로 창을 끌어올렸는데,
            // 채팅방 창이 닫힌 채로 두면 폴링이 몇 초마다 open 을 다시 시도하면서 카카오톡
            // 메인 창을 계속 화면 앞으로 튀어나오게 만들었다. 어차피 트레이에 내려가 있으면
            // 검색칸이 그려져 있지도 않아 성공할 수 없으므로, 그냥 이유를 알리고 물러난다.
            if (!Win32.IsWindowVisible(main))
            {
                return Fail("'" + chat + "' 채팅방을 대신 열어주지 못했습니다. 카카오톡 메인 창이 "
                    + "화면에 없습니다(트레이). 카카오톡을 열고 채팅방을 직접 열어주세요.");
            }

            IntPtr search = SearchField(main);
            if (search == IntPtr.Zero)
            {
                return Fail("'" + chat + "' 채팅방을 대신 열어주지 못했습니다. Windows 카카오톡은 "
                    + "채팅 목록을 접근성 API 에 내놓지 않아 방을 짚을 수 없고, 검색칸도 지금 "
                    + "그려져 있지 않습니다. 카카오톡에서 직접 열어주세요.");
            }

            try
            {
                SetControlText(search, chat);
                // 검색 결과가 그려질 시간을 준다. 목록을 읽을 수 없으니 시간으로 기다린다.
                Thread.Sleep(700);
                SendKeysTo(search, new int[] { Win32.VK_RETURN }, false);

                for (int i = 0; i < 40; i++)
                {
                    Thread.Sleep(150);
                    if (MatchChatWindow(chat).Kind == MatchKind.Found)
                    {
                        Console.WriteLine("opened");
                        return 0;
                    }
                }
            }
            finally
            {
                // 검색어를 남겨두면 사용자가 카카오톡을 볼 때 목록이 걸러진 채로 보인다.
                SetControlText(search, "");
            }

            return Fail("'" + chat + "' 채팅창이 열리지 않았습니다. 검색 결과를 읽을 수 없어 "
                + "이름이 정확할 때만 열립니다 — 카카오톡에서 직접 열어주세요.");
        }


        // MARK: - probe

        /// 진단용 트리 덤프. 카카오톡 버전이 바뀌어 클래스 이름이나 접근성 노출이 달라졌을 때
        /// 무엇이 어떻게 보이는지 눈으로 확인하는 통로다.
        private static int RunProbe(string chat)
        {
            HashSet<uint> pids = new HashSet<uint>(KakaoPids());
            Console.WriteLine("== 최상위 창 ==");
            foreach (IntPtr hWnd in Win32.TopLevelWindows(pids))
            {
                string title = Win32.TitleOf(hWnd);
                string className = Win32.ClassOf(hWnd);
                if (title.Length == 0 && className.StartsWith("tooltips")) continue;
                Win32.RECT rect = Win32.RectOf(hWnd);
                Console.WriteLine("  hwnd=" + hWnd.ToInt64() + " class='" + className + "' title='" + title
                    + "' visible=" + Win32.IsWindowVisible(hWnd) + " minimized=" + Win32.IsIconic(hWnd)
                    + " rect=" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom);
            }

            Console.WriteLine();
            Console.WriteLine("== 채팅방 창 ==");
            foreach (ChatWindow window in ChatWindows())
            {
                Console.WriteLine("  '" + window.Title + "' hwnd=" + window.Handle.ToInt64());
            }

            IntPtr target = IntPtr.Zero;
            string targetLabel = null;

            if (chat != null)
            {
                Match match = MatchChatWindow(chat);
                if (match.Kind == MatchKind.Found)
                {
                    target = match.Window.Handle;
                    targetLabel = match.Window.Title;
                }
                else
                {
                    Console.WriteLine();
                    Console.WriteLine("'" + chat + "' 채팅창을 찾지 못해 메인 창을 대신 덤프합니다.");
                }
            }
            if (target == IntPtr.Zero)
            {
                List<ChatWindow> windows = ChatWindows();
                if (windows.Count > 0)
                {
                    target = windows[0].Handle;
                    targetLabel = windows[0].Title;
                }
                else
                {
                    target = MainWindow();
                    targetLabel = "카카오톡 메인 창";
                }
            }
            if (target == IntPtr.Zero)
            {
                Console.WriteLine();
                Console.WriteLine("덤프할 창이 없습니다. 카카오톡에 로그인하고 채팅방을 열어주세요.");
                return 0;
            }

            Console.WriteLine();
            Console.WriteLine("== '" + targetLabel + "' 의 자식 컨트롤 (Win32) ==");
            foreach (IntPtr child in Win32.Descendants(target))
            {
                Win32.RECT rect = Win32.RectOf(child);
                Console.WriteLine("  class='" + Win32.ClassOf(child) + "' text='" + Win32.TextOf(child)
                    + "' visible=" + Win32.IsWindowVisible(child)
                    + " rect=" + rect.Left + "," + rect.Top + "," + rect.Right + "," + rect.Bottom);
            }

            Console.WriteLine();
            Console.WriteLine("== '" + targetLabel + "' 의 UI Automation 트리 ==");
            try
            {
                AutomationElement root = AutomationElement.FromHandle(target);
                if (root == null) Console.WriteLine("  (UIA 요소를 얻지 못했습니다)");
                else DumpAutomation(root, 1);
            }
            catch (Exception error)
            {
                Console.WriteLine("  (UIA 실패: " + error.Message + ")");
            }
            return 0;
        }

        private static void DumpAutomation(AutomationElement element, int depth)
        {
            if (depth > 6) return;
            foreach (AutomationElement child in ChildrenOf(element))
            {
                Rect bounds;
                string rect = BoundsOf(child, out bounds)
                    ? " rect=" + (int)bounds.Left + "," + (int)bounds.Top + "," + (int)bounds.Right + "," + (int)bounds.Bottom
                    : "";
                Console.WriteLine(new string(' ', depth * 2) + "- name='" + NameOf(child)
                    + "' class='" + ClassOf(child) + "' type=" + TypeOf(child) + rect);
                DumpAutomation(child, depth + 1);
            }
        }
    }
}
