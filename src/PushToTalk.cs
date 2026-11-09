using System;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SoundDeck
{
    /// <summary>
    /// Holds a push-to-talk key (or mouse side button) down while sounds play, so voice apps that use
    /// push-to-talk transmit them. Keys are sent as hardware scan codes, which Discord and most games accept.
    /// </summary>
    static class PushToTalk
    {
        [StructLayout(LayoutKind.Sequential)]
        struct MOUSEINPUT { public int dx, dy, mouseData, dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        struct KEYBDINPUT { public ushort wVk, wScan; public int dwFlags, time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        struct InputUnion { [FieldOffset(0)] public MOUSEINPUT mi; [FieldOffset(0)] public KEYBDINPUT ki; }

        [StructLayout(LayoutKind.Sequential)]
        struct INPUT { public int type; public InputUnion u; }

        [DllImport("user32.dll", SetLastError = true)] static extern int SendInput(int count, INPUT[] inputs, int size);
        [DllImport("user32.dll")] static extern int MapVirtualKey(int code, int mapType);

        const int INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        const int KEYEVENTF_EXTENDEDKEY = 0x1, KEYEVENTF_KEYUP = 0x2, KEYEVENTF_SCANCODE = 0x8;
        const int MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_XDOWN = 0x80, MOUSEEVENTF_XUP = 0x100;

        public static void Press(Keys k)
        {
            if ((k & Keys.Control) != 0) Send(Keys.LControlKey, false);
            if ((k & Keys.Shift) != 0) Send(Keys.LShiftKey, false);
            if ((k & Keys.Alt) != 0) Send(Keys.LMenu, false);
            Send(k & Keys.KeyCode, false);
        }

        public static void Release(Keys k)
        {
            Send(k & Keys.KeyCode, true);
            if ((k & Keys.Alt) != 0) Send(Keys.LMenu, true);
            if ((k & Keys.Shift) != 0) Send(Keys.LShiftKey, true);
            if ((k & Keys.Control) != 0) Send(Keys.LControlKey, true);
        }

        static void Send(Keys key, bool up)
        {
            INPUT input = new INPUT();
            if (key == Keys.XButton1 || key == Keys.XButton2 || key == Keys.MButton)
            {
                input.type = INPUT_MOUSE;
                if (key == Keys.MButton) input.u.mi.dwFlags = up ? MOUSEEVENTF_MIDDLEUP : MOUSEEVENTF_MIDDLEDOWN;
                else
                {
                    input.u.mi.dwFlags = up ? MOUSEEVENTF_XUP : MOUSEEVENTF_XDOWN;
                    input.u.mi.mouseData = key == Keys.XButton1 ? 1 : 2;
                }
            }
            else
            {
                input.type = INPUT_KEYBOARD;
                input.u.ki.wVk = (ushort)key;
                input.u.ki.wScan = (ushort)MapVirtualKey((int)key, 0);
                input.u.ki.dwFlags = KEYEVENTF_SCANCODE | (IsExtended(key) ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            }
            SendInput(1, new[] { input }, Marshal.SizeOf(typeof(INPUT)));
        }

        static bool IsExtended(Keys k)
        {
            switch (k)
            {
                case Keys.Insert: case Keys.Delete: case Keys.Home: case Keys.End: case Keys.PageUp: case Keys.PageDown:
                case Keys.Left: case Keys.Right: case Keys.Up: case Keys.Down: case Keys.RControlKey: case Keys.RMenu:
                case Keys.Divide: case Keys.NumLock: case Keys.PrintScreen: case Keys.Apps: case Keys.LWin: case Keys.RWin:
                    return true;
                default:
                    return false;
            }
        }

        public static string Describe(Keys k)
        {
            switch (k & Keys.KeyCode)
            {
                case Keys.XButton1: return "Mouse 4";
                case Keys.XButton2: return "Mouse 5";
                case Keys.MButton: return "Middle mouse";
            }
            return PadGrid.HotkeyText(k);
        }
    }
}
