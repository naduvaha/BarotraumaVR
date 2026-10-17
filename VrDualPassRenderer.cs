using System;

namespace BarotraumaVR
{
    /// <summary>
    /// Manages dual-pass rendering for VR stereo.
    /// Phase 2.2: Signal which eye is being rendered for VrSpriteBatchPatch.
    ///
    /// Pattern from CasualtyVR:
    /// - Don't modify Camera at all (it stays orthographic)
    /// - VrSpriteBatchPatch intercepts SpriteBatch.Begin() and replaces the matrix
    /// - Just control currentEyeIndex to tell VrSpriteBatchPatch which VR matrix to use
    /// </summary>
    public static class VrDualPassRenderer
    {
        private static bool isRendering = false;
        private static int beginLogCount = 0;

        /// <summary>
        /// Initialize dual-pass renderer.
        /// </summary>
        public static void Initialize()
        {
            VRManager.Log("[VrDualPassRenderer] Initialized - dual-pass ready");
        }

        /// <summary>
        /// Begin VR dual-pass rendering.
        /// Called from Draw_Prefix before game rendering starts.
        /// </summary>
        public static void BeginDualPass(object gameScreen)
        {
            if (isRendering)
            {
                VRManager.Log("[VrDualPassRenderer] WARNING: Already rendering!");
                return;
            }

            if (!VRManager.IsVRActive || !VRManager.StereoReady)
                return;

            try
            {
                isRendering = true;

                VrSpriteBatchPatch.SetCurrentEye(0);

                if (beginLogCount++ < 3)
                {
                    VRManager.Log("[VrDualPassRenderer] Diorama: cave behind the hull, crew in front of it");
                }
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrDualPassRenderer] BeginDualPass error: {ex.Message}");
                isRendering = false;
            }
        }

        /// <summary>
        /// End VR dual-pass rendering.
        /// Called from Draw_Postfix after game rendering finishes.
        /// </summary>
        public static void EndDualPass(object gameScreen)
        {
            if (!isRendering)
                return;

            try
            {
                // Don't reset currentEye here - keep it active for the entire VR session
                // It will be reset when VR is disabled via DisableVR()

                isRendering = false;
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrDualPassRenderer] EndDualPass error: {ex.Message}");
                isRendering = false;
            }
        }

        /// <summary>
        /// Check if we're currently in dual-pass rendering mode.
        /// </summary>
        public static bool IsRendering => isRendering;
    }
}
