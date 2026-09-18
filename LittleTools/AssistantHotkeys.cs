using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace LittleTools.Manager
{
    // Used only while the assistant is absent. Its own hotkey service takes over on launch.
    internal sealed class AssistantHotkeys : NativeWindow, IDisposable
    {
        private readonly Action<string> callback;
        private readonly HashSet<int> registered = new HashSet<int>();
        private readonly HookProc hookProc;
        private IntPtr hook;
        private int pressedKey;
        private bool disposed;

        public AssistantHotkeys(Action<string> callback)
        {
            this.callback = callback;
            CreateHandle(new CreateParams { Parent = new IntPtr(-3) });
            Register(1, 4, 8); // Shift+Backspace: translation
            Register(2, 2, 8); // Ctrl+Backspace: chat
            Register(3, 3, 0x58); // Ctrl+Alt+X: screenshot
            // Match the assistant's existing fallback when another application owns a shortcut.
            if (registered.Count != 3)
            {
                hookProc = KeyboardHook;
                hook = SetWindowsHookEx(13, hookProc, IntPtr.Zero, 0);
            }
        }

        private void Register(int id, uint modifiers, uint key)
        {
            if (RegisterHotKey(Handle, id, modifiers | 0x4000, key)) registered.Add(id);
        }

        private void Dispatch(int id)
        {
            callback(id == 1 ? "--translate" : id == 2 ? "--chat" : "--screenshot");
        }

        protected override void WndProc(ref Message message)
        {
            if (message.Msg == 0x312) Dispatch(message.WParam.ToInt32());
            base.WndProc(ref message);
        }

        private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
        {
            if (code >= 0 && !disposed)
            {
                int key = Marshal.ReadInt32(data);
                int kind = message.ToInt32();
                if ((kind == 0x101 || kind == 0x105) && pressedKey == key)
                {
                    pressedKey = 0;
                    return new IntPtr(1);
                }
                if (kind == 0x100 || kind == 0x104)
                {
                    if (pressedKey == key) return new IntPtr(1);
                    bool shift = Down(0x10), control = Down(0x11), alt = Down(0x12);
                    int id = key == 8 && !alt ? (shift && !control ? 1 : control && !shift ? 2 : 0)
                        : key == 0x58 && control && alt && !shift ? 3 : 0;
                    if (id != 0 && !registered.Contains(id))
                    {
                        pressedKey = key;
                        Dispatch(id);
                        return new IntPtr(1);
                    }
                }
            }
            return CallNextHookEx(hook, code, message, data);
        }

        private static bool Down(int key) { return (GetAsyncKeyState(key) & 0x8000) != 0; }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (int id in registered) UnregisterHotKey(Handle, id);
            if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
            DestroyHandle();
        }

        private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
        [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr h, int id, uint modifiers, uint key);
        [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
        [DllImport("user32.dll")] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
        [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
        [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    }
}
