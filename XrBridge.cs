using System;
using System.Runtime.InteropServices;

namespace BarotraumaVR
{
    /// <summary>
    /// P/Invoke wrapper for BarotraumaVR.XrBridge native DLL.
    /// Manages OpenXR session lifecycle and frame submission.
    /// </summary>
    public static class XrBridge
    {
        private const string DllName = "BarotraumaVR.XrBridge.dll";

        #region Structs matching native layout

        [StructLayout(LayoutKind.Sequential)]
        public struct Vec3
        {
            public float X, Y, Z;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Quat
        {
            public float X, Y, Z, W;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Fov
        {
            public float Left, Right, Up, Down;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct View
        {
            public Quat Orientation;
            public Vec3 Position;
            public Fov FieldOfView;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct FrameInfo
        {
            public long PredictedDisplayTime;
            public long PredictedDisplayPeriod;
            public int ShouldRender;
            public int ViewsValid;
            public ulong FrameIndex;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public View[] Views;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct Info
        {
            public int RecommendedWidth;
            public int RecommendedHeight;
            public int SwapchainFormat;
            public int SessionState;
            public int SessionRunning;
            public ulong FramesSubmitted;
            public ulong FramesDropped;
            public int LastSubmitResult;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string RuntimeName;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string SystemName;
            public uint ApiMajor;
            public uint ApiMinor;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct Hand
        {
            public Quat AimOrientation;
            public Vec3 AimPosition;
            public int PoseValid;
            public int Active;
            public float Trigger, Squeeze, StickX, StickY;
            public int StickClick;
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
        public struct Input
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 2)]
            public Hand[] Hands;
            public uint Buttons;
            public int Focused;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
            public string Profile;
        }

        #endregion

        #region Native imports

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_init(IntPtr gameTexture,
            [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder errorBuf,
            int errorLen);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_get_info(out Info info);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_poll();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_wait_frame(out FrameInfo frameInfo);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_set_source_texture(IntPtr texture);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_set_hud_texture(IntPtr texture);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_submit();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_begin_frame();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_end_frame();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_copy_source_to_acquired(int eyeIndex);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_overlay_hud(int eyeIndex);

        /// <summary>
        /// Phase 2.3: Submit separate textures for left and right eye.
        /// </summary>
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern int bvr_xr_submit_stereo(IntPtr leftEyeTexture, IntPtr rightEyeTexture);

        // Returns ID3D11Texture2D* for this frame. Do not call in the same frame as bvr_xr_submit:
        // that function acquires the same swapchain image itself.
        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern IntPtr bvr_xr_acquire_swapchain(int eyeIndex);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_release_swapchain(int eyeIndex);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_shutdown();

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_get_log(
            [MarshalAs(UnmanagedType.LPStr)] System.Text.StringBuilder buf,
            int len);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_get_input(out Input input);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_get_picture_rect(out int x, out int y, out int w, out int h);

        [DllImport(DllName, CallingConvention = CallingConvention.Cdecl)]
        public static extern void bvr_xr_haptic(int hand, float amplitude, float seconds);

        #endregion

        #region Helper methods

        /// <summary>
        /// Drain and return the native bridge's log buffer.
        /// </summary>
        public static string GetLog()
        {
            var buf = new System.Text.StringBuilder(16384);
            bvr_xr_get_log(buf, buf.Capacity);
            return buf.ToString();
        }

        /// <summary>
        /// Initialize OpenXR with error reporting.
        /// </summary>
        public static bool Initialize(IntPtr gameTexture, out string error)
        {
            var errorBuf = new System.Text.StringBuilder(512);
            int result = bvr_xr_init(gameTexture, errorBuf, errorBuf.Capacity);
            error = errorBuf.ToString();

            return result != 0;
        }

        #endregion
    }
}
