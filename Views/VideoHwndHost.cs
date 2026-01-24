using LibVLCSharp.Shared;
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace AIWeather.Views
{
    public class VideoHwndHost : HwndHost
    {
        private IntPtr _hwnd;
        private IntPtr _parentHwnd;

        private const int WS_EX_LAYERED = 0x00080000;
        private const int LWA_COLORKEY = 0x00000001;

        private const int WS_CHILD = 0x40000000;
        private const int WS_VISIBLE = 0x10000000;

        public IntPtr Hwnd => _hwnd;

        public MediaPlayer Player { get; }

        public VideoHwndHost(MediaPlayer player)
        {
            Player = player;
        }

        public void ResizeTo(double width, double height)
        {
            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            var targetWidth = Math.Max(1, (int)Math.Round(width));
            var targetHeight = Math.Max(1, (int)Math.Round(height));
            SetWindowPos(_hwnd, IntPtr.Zero, 0, 0, targetWidth, targetHeight, SWP_NOZORDER | SWP_NOACTIVATE);
        }

        protected override HandleRef BuildWindowCore(HandleRef hwndParent)
        {
            _parentHwnd = hwndParent.Handle;

            // Create with a small initial size; WPF will call OnWindowPositionChanged
            // and we'll resize to the actual layout size.
            const int width = 1;
            const int height = 1;

            // Create a transparent, layered child window to host the video output.
            // This matches the RTSP Client plugin approach and avoids visible backplates.
            _hwnd = CreateWindowEx(
                WS_EX_LAYERED,
                "Static",
                "",
                WS_CHILD | WS_VISIBLE,
                0, 0,
                width, height,
                _parentHwnd,
                IntPtr.Zero,
                IntPtr.Zero,
                IntPtr.Zero);

            if (_hwnd == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed for video host window");
            }

            // Make the background fully transparent via color key (black).
            // LibVLC will render the video content over this window.
            SetLayeredWindowAttributes(_hwnd, 0, 0, LWA_COLORKEY);

            return new HandleRef(this, _hwnd);
        }

        protected override void OnWindowPositionChanged(System.Windows.Rect rcBoundingBox)
        {
            base.OnWindowPositionChanged(rcBoundingBox);

            if (_hwnd == IntPtr.Zero)
            {
                return;
            }

            ResizeTo(rcBoundingBox.Width, rcBoundingBox.Height);
        }

        protected override void DestroyWindowCore(HandleRef hwnd)
        {
            if (_hwnd != IntPtr.Zero)
            {
                DestroyWindow(_hwnd);
                _hwnd = IntPtr.Zero;
            }
        }

        protected override IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            return base.WndProc(hwnd, msg, wParam, lParam, ref handled);
        }

        [DllImport("user32.dll", EntryPoint = "CreateWindowEx", SetLastError = true, CharSet = CharSet.Auto)]
        private static extern IntPtr CreateWindowEx(
            int dwExStyle,
            string lpClassName,
            string lpWindowName,
            int dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            IntPtr hwndParent,
            IntPtr hMenu,
            IntPtr hInst,
            IntPtr lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool SetLayeredWindowAttributes(
            IntPtr hwnd,
            uint crKey,
            byte bAlpha,
            uint dwFlags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool DestroyWindow(IntPtr hWnd);

        private const int SWP_NOZORDER = 0x0004;
        private const int SWP_NOACTIVATE = 0x0010;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(
            IntPtr hWnd,
            IntPtr hWndInsertAfter,
            int x,
            int y,
            int cx,
            int cy,
            int uFlags);

    }
}
