// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using Fieldkit.Core;

namespace Fieldkit.Interop;

/// <summary>A keyboard event seen by the low-level hook.</summary>
public struct KeyEvent
{
    public int VirtualKey;
    public int ScanCode;
    public bool IsDown;
    /// <summary>True for WM_SYSKEYDOWN/UP (Alt held, or F10).</summary>
    public bool IsSystem;
    /// <summary>Injected by any process (SendInput).</summary>
    public bool IsInjected;
    /// <summary>Injected by this app (see <see cref="InputSender.Tag"/>).</summary>
    public bool IsOwn;
    public uint Time;
    public uint Flags;
}

public enum MouseAction { Move, LeftDown, LeftUp, RightDown, RightUp, MiddleDown, MiddleUp, XDown, XUp, Wheel, HWheel }

/// <summary>A mouse event seen by the low-level hook.</summary>
public struct MouseEvent
{
    public MouseAction Action;
    public int X;
    public int Y;
    /// <summary>Wheel delta (multiples of 120 for notched wheels) for Wheel/HWheel.</summary>
    public int WheelDelta;
    /// <summary>1 = XBUTTON1 (back), 2 = XBUTTON2 (forward) for XDown/XUp.</summary>
    public int XButton;
    public bool IsInjected;
    public bool IsOwn;
    public uint Time;
}

/// <summary>Return true to swallow the event (it never reaches other apps).</summary>
public delegate bool KeyHandler(ref KeyEvent e);

/// <summary>Return true to swallow the event (it never reaches other apps).</summary>
public delegate bool MouseHandler(ref MouseEvent e);

/// <summary>
/// One shared WH_KEYBOARD_LL and one WH_MOUSE_LL hook for the whole app, installed on a
/// dedicated thread with its own message loop (so a busy UI thread never makes Windows
/// time the hook out). Hooks are installed when the first handler is added and removed
/// with the last one, so idle features cost nothing.
///
/// HANDLERS RUN ON THE HOOK THREAD. Keep them fast (well under 1 ms), never block, and
/// marshal UI work with <c>Application.Current.Dispatcher.BeginInvoke</c>. Handlers are
/// called in ascending <c>priority</c>; the first that returns true swallows the event
/// and later handlers do not see it.
/// </summary>
public static class InputHooks
{
    private static readonly object Gate = new();
    private static readonly List<(int Priority, KeyHandler Handler)> KeyHandlers = [];
    private static readonly List<(int Priority, MouseHandler Handler)> MouseHandlers = [];
    private static KeyHandler[] _keySnapshot = [];
    private static MouseHandler[] _mouseSnapshot = [];

    private static Thread? _thread;
    private static uint _threadId;
    private static IntPtr _keyHook;
    private static IntPtr _mouseHook;
    private static readonly Native.LowLevelProc KeyProc = KeyboardCallback;
    private static readonly Native.LowLevelProc MouseProc = MouseCallback;
    private static readonly AutoResetEvent Ready = new(false);

    private const uint WM_APP_SYNC = 0x8000 + 1;
    private const uint WM_APP_REINSTALL = 0x8000 + 2;

    public static IDisposable AddKeyboardHandler(KeyHandler handler, int priority = 100)
    {
        lock (Gate)
        {
            KeyHandlers.Add((priority, handler));
            _keySnapshot = KeyHandlers.OrderBy(h => h.Priority).Select(h => h.Handler).ToArray();
            Sync();
        }
        return new Token(() =>
        {
            lock (Gate)
            {
                KeyHandlers.RemoveAll(h => h.Handler == handler);
                _keySnapshot = KeyHandlers.OrderBy(h => h.Priority).Select(h => h.Handler).ToArray();
                Sync();
            }
        });
    }

    public static IDisposable AddMouseHandler(MouseHandler handler, int priority = 100)
    {
        lock (Gate)
        {
            MouseHandlers.Add((priority, handler));
            _mouseSnapshot = MouseHandlers.OrderBy(h => h.Priority).Select(h => h.Handler).ToArray();
            Sync();
        }
        return new Token(() =>
        {
            lock (Gate)
            {
                MouseHandlers.RemoveAll(h => h.Handler == handler);
                _mouseSnapshot = MouseHandlers.OrderBy(h => h.Priority).Select(h => h.Handler).ToArray();
                Sync();
            }
        });
    }

    /// <summary>
    /// Removes and reinstalls the hooks. Windows silently drops a low-level hook that once answers
    /// too slowly (common around sleep, resume and session switches), which leaves every input
    /// feature dead until restart (upstream #1075); the app calls this after those events.
    /// </summary>
    public static void Reinstall()
    {
        lock (Gate)
        {
            if (_thread is not null) Native.PostThreadMessage(_threadId, WM_APP_REINSTALL, IntPtr.Zero, IntPtr.Zero);
        }
    }

    /// <summary>Whether a key is physically down right now (async state).</summary>
    public static bool IsKeyDown(int vk) => (Native.GetAsyncKeyState(vk) & 0x8000) != 0;

    public static void Shutdown()
    {
        lock (Gate)
        {
            KeyHandlers.Clear();
            MouseHandlers.Clear();
            _keySnapshot = [];
            _mouseSnapshot = [];
            if (_thread is not null) Native.PostThreadMessage(_threadId, Native.WM_QUIT, IntPtr.Zero, IntPtr.Zero);
        }
    }

    // Called under Gate: make the hook thread install/remove hooks to match the handler lists.
    private static void Sync()
    {
        if (_thread is null)
        {
            if (KeyHandlers.Count == 0 && MouseHandlers.Count == 0) return;
            _thread = new Thread(HookThread) { IsBackground = true, Name = "Fieldkit input hooks", Priority = ThreadPriority.Highest };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
            Ready.WaitOne(2000);
        }
        Native.PostThreadMessage(_threadId, WM_APP_SYNC, IntPtr.Zero, IntPtr.Zero);
    }

    private static void HookThread()
    {
        _threadId = Native.GetCurrentThreadId();
        // Force creation of the thread's message queue before signalling.
        Native.PostThreadMessage(_threadId, 0, IntPtr.Zero, IntPtr.Zero);
        Ready.Set();
        while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
        {
            if (msg.message == WM_APP_SYNC)
            {
                ApplyHooks();
                continue;
            }
            if (msg.message == WM_APP_REINSTALL)
            {
                RemoveHooks();
                ApplyHooks();
                continue;
            }
            Native.TranslateMessage(ref msg);
            Native.DispatchMessage(ref msg);
        }
        RemoveHooks();
    }

    private static void ApplyHooks()
    {
        bool wantKey, wantMouse;
        lock (Gate)
        {
            wantKey = KeyHandlers.Count > 0;
            wantMouse = MouseHandlers.Count > 0;
        }
        var module = Native.GetModuleHandle(null);
        if (wantKey && _keyHook == IntPtr.Zero)
        {
            _keyHook = Native.SetWindowsHookEx(Native.WH_KEYBOARD_LL, KeyProc, module, 0);
            if (_keyHook == IntPtr.Zero) Log.Error($"Keyboard hook failed: {Marshal.GetLastWin32Error()}");
        }
        else if (!wantKey && _keyHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_keyHook);
            _keyHook = IntPtr.Zero;
        }
        if (wantMouse && _mouseHook == IntPtr.Zero)
        {
            _mouseHook = Native.SetWindowsHookEx(Native.WH_MOUSE_LL, MouseProc, module, 0);
            if (_mouseHook == IntPtr.Zero) Log.Error($"Mouse hook failed: {Marshal.GetLastWin32Error()}");
        }
        else if (!wantMouse && _mouseHook != IntPtr.Zero)
        {
            Native.UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }
    }

    private static void RemoveHooks()
    {
        if (_keyHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_keyHook);
        if (_mouseHook != IntPtr.Zero) Native.UnhookWindowsHookEx(_mouseHook);
        _keyHook = _mouseHook = IntPtr.Zero;
    }

    private static IntPtr KeyboardCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.KBDLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var e = new KeyEvent
            {
                VirtualKey = (int)data.vkCode,
                ScanCode = (int)data.scanCode,
                IsDown = msg is Native.WM_KEYDOWN or Native.WM_SYSKEYDOWN,
                IsSystem = msg is Native.WM_SYSKEYDOWN or Native.WM_SYSKEYUP,
                IsInjected = (data.flags & Native.LLKHF_INJECTED) != 0,
                IsOwn = data.dwExtraInfo == InputSender.Tag,
                Time = data.time,
                Flags = data.flags,
            };
            foreach (var handler in _keySnapshot)
            {
                try
                {
                    if (handler(ref e)) return 1;
                }
                catch (Exception ex)
                {
                    Log.Error("Keyboard handler threw", ex);
                }
            }
        }
        return Native.CallNextHookEx(_keyHook, nCode, wParam, lParam);
    }

    private static IntPtr MouseCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var data = Marshal.PtrToStructure<Native.MSLLHOOKSTRUCT>(lParam);
            var msg = (int)wParam;
            var e = new MouseEvent
            {
                X = data.pt.X,
                Y = data.pt.Y,
                IsInjected = (data.flags & Native.LLMHF_INJECTED) != 0,
                IsOwn = data.dwExtraInfo == InputSender.Tag,
                Time = data.time,
            };
            switch (msg)
            {
                case Native.WM_MOUSEMOVE: e.Action = MouseAction.Move; break;
                case Native.WM_LBUTTONDOWN: e.Action = MouseAction.LeftDown; break;
                case Native.WM_LBUTTONUP: e.Action = MouseAction.LeftUp; break;
                case Native.WM_RBUTTONDOWN: e.Action = MouseAction.RightDown; break;
                case Native.WM_RBUTTONUP: e.Action = MouseAction.RightUp; break;
                case Native.WM_MBUTTONDOWN: e.Action = MouseAction.MiddleDown; break;
                case Native.WM_MBUTTONUP: e.Action = MouseAction.MiddleUp; break;
                case Native.WM_XBUTTONDOWN: e.Action = MouseAction.XDown; e.XButton = (int)(data.mouseData >> 16); break;
                case Native.WM_XBUTTONUP: e.Action = MouseAction.XUp; e.XButton = (int)(data.mouseData >> 16); break;
                case Native.WM_MOUSEWHEEL: e.Action = MouseAction.Wheel; e.WheelDelta = (short)(data.mouseData >> 16); break;
                case Native.WM_MOUSEHWHEEL: e.Action = MouseAction.HWheel; e.WheelDelta = (short)(data.mouseData >> 16); break;
                default: return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
            }
            foreach (var handler in _mouseSnapshot)
            {
                try
                {
                    if (handler(ref e)) return 1;
                }
                catch (Exception ex)
                {
                    Log.Error("Mouse handler threw", ex);
                }
            }
        }
        return Native.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private sealed class Token(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() { _dispose?.Invoke(); _dispose = null; }
    }
}
