using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace BalanceDock.Native
{
    public static class DwmBackdrop
    {
        private const int DwmwaUseImmersiveDarkMode = 20;
        private const int DwmwaWindowCornerPreference = 33;
        private const int DwmwaBorderColor = 34;
        private const int DwmwaSystemBackdropType = 38;
        private const int DwmRound = 2;
        private const int DwmTransientWindow = 3;
        private const uint DwmColorNone = 0xFFFFFFFE;
        private const int WcaAccentPolicy = 19;
        private const int AccentEnableAcrylicBlurBehind = 4;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        // Do not let USER32 copy the old client pixels while the acrylic window
        // is shrinking.  The copied frame can contain the expanded balance
        // surface and is presented for one frame before WPF repaints it.
        private const uint SwpNoCopyBits = 0x0100;
        private const uint SwpNoOwnerZOrder = 0x0200;

        [StructLayout(LayoutKind.Sequential)]
        private struct Margins
        {
            public int Left;
            public int Right;
            public int Top;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public int AccentState;
            public int AccentFlags;
            public int GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref uint value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref Margins margins);

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool SetWindowPos(IntPtr hwnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

        [DllImport("user32.dll")]
        private static extern bool ReleaseCapture();

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hwnd, uint message, IntPtr wParam, IntPtr lParam);

        public static void Apply(Window window, bool dark)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;

            try
            {
                var source = HwndSource.FromHwnd(handle);
                if (source != null && source.CompositionTarget != null)
                    source.CompositionTarget.BackgroundColor = Colors.Transparent;

                var margins = new Margins { Left = -1, Right = -1, Top = -1, Bottom = -1 };
                DwmExtendFrameIntoClientArea(handle, ref margins);

                int darkValue = dark ? 1 : 0;
                int corner = DwmRound;
                int backdrop = DwmTransientWindow;
                uint border = DwmColorNone;
                DwmSetWindowAttribute(handle, DwmwaUseImmersiveDarkMode, ref darkValue, sizeof(int));
                DwmSetWindowAttribute(handle, DwmwaWindowCornerPreference, ref corner, sizeof(int));
                DwmSetWindowAttribute(handle, DwmwaSystemBackdropType, ref backdrop, sizeof(int));
                DwmSetWindowAttribute(handle, DwmwaBorderColor, ref border, sizeof(uint));
                ApplyAcrylic(handle, dark);
            }
            catch
            {
                // The translucent WPF surface remains usable on older Windows builds.
            }
        }

        public static bool TrySetBounds(Window window, double left, double top, double width, double height)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            var source = handle == IntPtr.Zero ? null : HwndSource.FromHwnd(handle);
            if (source == null || source.CompositionTarget == null) return false;

            Matrix toDevice = source.CompositionTarget.TransformToDevice;
            Point position = toDevice.Transform(new Point(left, top));
            Point size = toDevice.Transform(new Point(width, height));
            return SetWindowPos(handle, IntPtr.Zero,
                (int)Math.Round(position.X), (int)Math.Round(position.Y),
                Math.Max(1, (int)Math.Round(size.X)), Math.Max(1, (int)Math.Round(size.Y)),
                SwpNoZOrder | SwpNoActivate | SwpNoCopyBits | SwpNoOwnerZOrder);
        }

        public static bool BeginWindowDrag(Window window)
        {
            IntPtr handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return false;
            ReleaseCapture();
            SendMessage(handle, 0x00A1, new IntPtr(2), IntPtr.Zero);
            return true;
        }

        private static void ApplyAcrylic(IntPtr handle, bool dark)
        {
            var accent = new AccentPolicy
            {
                AccentState = AccentEnableAcrylicBlurBehind,
                AccentFlags = 2,
                GradientColor = dark ? unchecked((int)0xCC181818) : unchecked((int)0xCCFAFAFA),
                AnimationId = 0
            };
            int size = Marshal.SizeOf(typeof(AccentPolicy));
            IntPtr pointer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, pointer, false);
                var data = new WindowCompositionAttributeData { Attribute = WcaAccentPolicy, Data = pointer, SizeOfData = size };
                SetWindowCompositionAttribute(handle, ref data);
            }
            finally { Marshal.FreeHGlobal(pointer); }
        }
    }
}
