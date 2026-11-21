using System;
using System.Collections.Generic;
using System.Windows.Input;
using System.Windows.Interop;

namespace LuminaRecorder.Services
{
    /// <summary>System-wide hotkeys (work even when the app is minimised or another program has focus).</summary>
    public sealed class HotkeyService : IDisposable
    {
        readonly IntPtr _hwnd;
        readonly HwndSource _source;
        readonly Dictionary<int, Action> _actions = new Dictionary<int, Action>();
        int _nextId = 0xB000;

        public HotkeyService(IntPtr hwnd)
        {
            _hwnd = hwnd;
            _source = HwndSource.FromHwnd(hwnd);
            _source.AddHook(WndProc);
        }

        /// <summary>Registers e.g. "F9", "Ctrl+Shift+R". Returns false if the text is invalid or another app already owns it.</summary>
        public bool Register(string gesture, Action action)
        {
            if (!TryParse(gesture, out uint mods, out uint vk)) return false;
            int id = _nextId++;
            if (!Native.RegisterHotKey(_hwnd, id, mods | Native.MOD_NOREPEAT, vk)) return false;
            _actions[id] = action;
            return true;
        }

        public void UnregisterAll()
        {
            foreach (var id in _actions.Keys) Native.UnregisterHotKey(_hwnd, id);
            _actions.Clear();
        }

        public static bool TryParse(string gesture, out uint modifiers, out uint vk)
        {
            modifiers = 0; vk = 0;
            if (string.IsNullOrWhiteSpace(gesture)) return false;
            Key key = Key.None;
            foreach (var raw in gesture.Split('+'))
            {
                var part = raw.Trim();
                switch (part.ToLowerInvariant())
                {
                    case "ctrl": case "control": modifiers |= Native.MOD_CONTROL; break;
                    case "shift": modifiers |= Native.MOD_SHIFT; break;
                    case "alt": modifiers |= Native.MOD_ALT; break;
                    default:
                        if (!Enum.TryParse(part, true, out key)) return false;
                        break;
                }
            }
            if (key == Key.None) return false;
            vk = (uint)KeyInterop.VirtualKeyFromKey(key);
            return vk != 0;
        }

        IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == Native.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
            {
                handled = true;
                action();
            }
            return IntPtr.Zero;
        }

        public void Dispose()
        {
            UnregisterAll();
            _source?.RemoveHook(WndProc);
        }
    }
}
