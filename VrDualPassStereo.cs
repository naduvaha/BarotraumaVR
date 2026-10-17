using System;
using System.Reflection;

namespace BarotraumaVR
{
    /// <summary>
    /// Phase 2.3: True dual-pass stereo rendering.
    /// Renders game twice per frame: once to leftEyeRT, once to rightEyeRT,
    /// then submits both textures to OpenXR via bvr_xr_submit_stereo().
    /// </summary>
    public static class VrDualPassStereo
    {
        private static Type? graphicsDeviceType;
        private static MethodInfo? setRenderTargetMethod;
        private static MethodInfo? getRenderTargetMethod;
        private static object? originalRenderTarget;
        private static bool initialized = false;
        private static int submitLogCount = 0;

        public static void Initialize(object graphicsDevice)
        {
            if (initialized)
                return;

            try
            {
                graphicsDeviceType = graphicsDevice.GetType();

                // Find GraphicsDevice.SetRenderTarget(RenderTarget2D)
                setRenderTargetMethod = graphicsDeviceType.GetMethod("SetRenderTarget",
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { VRManager.FindType("Microsoft.Xna.Framework.Graphics.RenderTarget2D") ?? typeof(object) },
                    null);

                // Find GraphicsDevice.GetRenderTargets() or similar
                getRenderTargetMethod = graphicsDeviceType.GetMethod("GetRenderTargets",
                    BindingFlags.Public | BindingFlags.Instance);

                if (setRenderTargetMethod == null)
                {
                    VRManager.Log("[VrDualPassStereo] WARNING: SetRenderTarget method not found");
                }
                else
                {
                    VRManager.Log("[VrDualPassStereo] Initialized - dual-pass stereo ready");
                    initialized = true;
                }
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrDualPassStereo] Initialize error: {ex.Message}");
            }
        }

        /// <summary>
        /// Perform dual-pass rendering: render game to leftEyeRT, then rightEyeRT,
        /// then submit both to OpenXR via bvr_xr_submit_stereo().
        ///
        /// Called from GameScreen.Draw Postfix after game has rendered to backbuffer.
        /// </summary>
        public static void RenderAndSubmitStereo(object graphicsDevice)
        {
            if (!initialized || !VRManager.IsVRActive || !VrStereoRenderer.AreRenderTargetsReady())
                return;

            try
            {
                // For Phase 2.3, we're using Theatre mode approach:
                // Game already rendered to backbuffer with 2D orthographic projection.
                // We just need to get the RenderTarget textures and submit them.

                // TODO Phase 2.4: Actually render game twice with different VR cameras
                // For now, get texture pointers from RenderTargets and submit

                var leftEyePtr = VrStereoRenderer.GetEyeTexturePointer(0);
                var rightEyePtr = VrStereoRenderer.GetEyeTexturePointer(1);

                if (leftEyePtr == IntPtr.Zero || rightEyePtr == IntPtr.Zero)
                {
                    if (submitLogCount++ < 3)
                    {
                        VRManager.Log($"[VrDualPassStereo] Eye texture pointers not ready (left: 0x{leftEyePtr:X}, right: 0x{rightEyePtr:X})");
                    }

                    // Fallback to theatre mode (single backbuffer)
                    XrBridge.bvr_xr_submit();
                    return;
                }

                // Submit stereo textures to OpenXR
                int result = XrBridge.bvr_xr_submit_stereo(leftEyePtr, rightEyePtr);

                if (submitLogCount++ < 3)
                {
                    VRManager.Log($"[VrDualPassStereo] Submitted stereo frame: left=0x{leftEyePtr:X}, right=0x{rightEyePtr:X}, result={result}");
                }
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrDualPassStereo] RenderAndSubmitStereo error: {ex.Message}");

                // Fallback to theatre mode on error
                XrBridge.bvr_xr_submit();
            }
        }

        /// <summary>
        /// Set GraphicsDevice render target (reflection-based).
        /// </summary>
        private static void SetRenderTarget(object graphicsDevice, object? renderTarget)
        {
            if (setRenderTargetMethod == null)
                return;

            setRenderTargetMethod.Invoke(graphicsDevice, new[] { renderTarget });
        }

        /// <summary>
        /// Save current render target for restoration.
        /// </summary>
        private static void SaveOriginalRenderTarget(object graphicsDevice)
        {
            if (getRenderTargetMethod == null)
                return;

            try
            {
                var targets = getRenderTargetMethod.Invoke(graphicsDevice, null);
                originalRenderTarget = targets; // Array of RenderTargetBinding or similar
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrDualPassStereo] SaveOriginalRenderTarget error: {ex.Message}");
            }
        }

        /// <summary>
        /// Restore original render target (backbuffer).
        /// </summary>
        private static void RestoreOriginalRenderTarget(object graphicsDevice)
        {
            SetRenderTarget(graphicsDevice, null); // null = backbuffer
        }
    }
}
