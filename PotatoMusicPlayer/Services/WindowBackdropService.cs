using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace PotatoMusicPlayer.Services
{
    /// <summary>
    /// DWM backdrop (acrylic / blur-behind) for the main window.
    /// The window stays non-layered (AllowsTransparency=False), so text keeps
    /// ClearType and only the window background becomes see-through.
    /// Panels, buttons and text keep their opaque theme brushes.
    /// </summary>
    public static class WindowBackdropService
    {
        private enum AccentState
        {
            Disabled = 0,
            BlurBehind = 3,
            AcrylicBlurBehind = 4
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct AccentPolicy
        {
            public AccentState AccentState;
            public int AccentFlags;
            public uint GradientColor;
            public int AnimationId;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WindowCompositionAttributeData
        {
            public int Attribute;
            public IntPtr Data;
            public int SizeOfData;
        }

        [DllImport("user32.dll")]
        private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

        private const int WcaAccentPolicy = 19;

        // Tintなしのクリアなすりガラスにするため、acrylic層にも色を付けない。
        private const uint AcrylicTint = 0x00000000;

        /// <summary>Acrylic を試し、不可なら blur-behind に落とす。いずれも不可なら false。</summary>
        public static bool TryEnableBackdrop(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return false;

            if (ApplyAccent(hwnd, AccentState.AcrylicBlurBehind, AcrylicTint))
                return true;
            return ApplyAccent(hwnd, AccentState.BlurBehind, 0);
        }

        public static void DisableBackdrop(Window window)
        {
            IntPtr hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero)
                return;
            ApplyAccent(hwnd, AccentState.Disabled, 0);
        }

        private static bool ApplyAccent(IntPtr hwnd, AccentState state, uint tint)
        {
            try
            {
                var policy = new AccentPolicy
                {
                    AccentState = state,
                    AccentFlags = 0,
                    GradientColor = tint,
                    AnimationId = 0
                };
                int size = Marshal.SizeOf(policy);
                IntPtr ptr = Marshal.AllocHGlobal(size);
                try
                {
                    Marshal.StructureToPtr(policy, ptr, false);
                    var data = new WindowCompositionAttributeData
                    {
                        Attribute = WcaAccentPolicy,
                        Data = ptr,
                        SizeOfData = size
                    };
                    return SetWindowCompositionAttribute(hwnd, ref data) != 0;
                }
                finally
                {
                    Marshal.FreeHGlobal(ptr);
                }
            }
            catch
            {
                return false;
            }
        }
    }
}
