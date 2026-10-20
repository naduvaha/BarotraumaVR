using System;
using System.Linq;
using System.Numerics;
using System.Reflection;

namespace BarotraumaVR
{
    /// <summary>
    /// Manages stereo (dual-eye) rendering for VR.
    /// Phase 2: Renders game twice per frame with different perspective matrices.
    ///
    /// Pattern from CasualtyVR:
    /// - Main thread: xrWaitFrame (cache poses)
    /// - Render thread: Draw left eye, draw right eye, xrEndFrame
    /// </summary>
    public static class VrStereoRenderer
    {
        private static XrBridge.FrameInfo currentFrame;
        private static bool frameReady = false;
        private static bool loggedFirstFrame = false;
        private static int applyTransformLogCount = 0;
        private static int renderEyeLogCount = 0;
        private static Matrix4x4 dioramaWorld;

        /// <summary>
        /// Midpoint of the two eye positions (the head), in tracking space,
        /// for first-person camera parallax.
        /// </summary>
        public static System.Numerics.Vector3 CenterEye { get; private set; }
        public static bool HeadViewValid { get; private set; }

        // Render targets for each eye (Phase 2.3)
        private static object? leftEyeTarget;
        private static object? rightEyeTarget;
        private static object? graphicsDevice;

        // MonoGame types (reflection-based access)
        private static Type? cameraType;
        private static Type? renderTargetType;
        private static Type? graphicsDeviceType;
        private static PropertyInfo? viewMatrixProperty;
        private static PropertyInfo? projectionMatrixProperty;

        /// <summary>
        /// Initialize stereo renderer - find Barotrauma's camera system.
        /// </summary>
        public static void Initialize()
        {
            cameraType = VRManager.FindType("Barotrauma.Camera");
            if (cameraType == null)
            {
                VRManager.Log("[VrStereoRenderer] Camera type not found - will retry");
                return;
            }

            // Barotrauma.Camera exposes Transform and ShaderTransform, not ViewMatrix/ProjectionMatrix.
            viewMatrixProperty = cameraType.GetProperty("Transform", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);
            projectionMatrixProperty = cameraType.GetProperty("ShaderTransform", BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

            if (viewMatrixProperty == null && projectionMatrixProperty == null)
            {
                var names = string.Join(", ", cameraType.GetProperties().Select(p => p.Name));
                VRManager.Log("[VrStereoRenderer] Camera.Transform and ShaderTransform not found. Properties: " + names);
                return;
            }

            VRManager.Log("[VrStereoRenderer] Using Camera." +
                (viewMatrixProperty?.Name ?? "(no Transform)") + " and Camera." +
                (projectionMatrixProperty?.Name ?? "(no ShaderTransform)"));

            // Find MonoGame types for RenderTarget creation (Phase 2.3)
            graphicsDeviceType = VRManager.FindType("Microsoft.Xna.Framework.Graphics.GraphicsDevice");
            renderTargetType = VRManager.FindType("Microsoft.Xna.Framework.Graphics.RenderTarget2D");

            if (graphicsDeviceType == null || renderTargetType == null)
            {
                VRManager.Log("[VrStereoRenderer] MonoGame GraphicsDevice/RenderTarget2D not found");
                return;
            }

            VRManager.Log("[VrStereoRenderer] MonoGame types found - ready for RenderTarget creation");

            // Create default diorama positioning
            // Barotrauma uses very large world coordinates (submarines are 50-100 meters)
            // Scale aggressively to fit submarine in comfortable viewing distance
            dioramaWorld = VrCamera.CreateDioramaWorldMatrix(
                distanceMetres: 2.5f,    // 2.5m in front of player (further back)
                scale: 0.002f,           // 1 game unit = 2mm (much smaller for large submarine)
                rotation: 0f
            );

            VRManager.Log("[VrStereoRenderer] Initialized - stereo rendering ready");
        }

        /// <summary>
        /// Create RenderTarget2D for each eye (Phase 2.3).
        /// Called when VR is enabled and we have GraphicsDevice.
        /// </summary>
        public static void CreateEyeRenderTargets(object device, int width, int height)
        {
            if (renderTargetType == null || graphicsDeviceType == null)
            {
                VRManager.Log("[VrStereoRenderer] Cannot create RenderTargets - types not found");
                return;
            }

            try
            {
                graphicsDevice = device;

                // Dispose old targets if they exist
                (leftEyeTarget as IDisposable)?.Dispose();
                (rightEyeTarget as IDisposable)?.Dispose();

                // Create new RenderTarget2D(GraphicsDevice, width, height)
                leftEyeTarget = Activator.CreateInstance(renderTargetType, device, width, height);
                rightEyeTarget = Activator.CreateInstance(renderTargetType, device, width, height);

                VRManager.Log($"[VrStereoRenderer] Created eye RenderTargets: {width}x{height}");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrStereoRenderer] Failed to create RenderTargets: {ex.Message}");
            }
        }

        /// <summary>
        /// Get native texture pointer for an eye's RenderTarget (for XrBridge submission).
        /// </summary>
        public static IntPtr GetEyeTexturePointer(int eyeIndex)
        {
            var target = eyeIndex == 0 ? leftEyeTarget : rightEyeTarget;
            if (target == null)
                return IntPtr.Zero;

            try
            {
                // MonoGame/FNA Texture2D has GetNativeTexturePtr() method
                var method = renderTargetType?.GetMethod("GetNativeTexturePtr",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                if (method != null)
                {
                    var result = method.Invoke(target, null);
                    return result is IntPtr ptr ? ptr : IntPtr.Zero;
                }

                // Alternative: try "Handle" property (D3D11 texture pointer)
                var handleProp = renderTargetType?.GetProperty("Handle",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic);

                if (handleProp != null)
                {
                    var result = handleProp.GetValue(target);
                    return result is IntPtr ptr ? ptr : IntPtr.Zero;
                }

                VRManager.Log("[VrStereoRenderer] Could not find texture pointer accessor");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrStereoRenderer] Failed to get texture pointer: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// Get eye RenderTarget object for GraphicsDevice.SetRenderTarget() calls.
        /// </summary>
        public static object? GetEyeRenderTarget(int eyeIndex)
        {
            return eyeIndex == 0 ? leftEyeTarget : rightEyeTarget;
        }

        /// <summary>
        /// Check if eye RenderTargets are created.
        /// </summary>
        public static bool AreRenderTargetsReady()
        {
            return leftEyeTarget != null && rightEyeTarget != null;
        }

        /// <summary>
        /// Update frame info from XrBridge (called by VRManager after xrWaitFrame).
        /// </summary>
        public static void UpdateFrame(XrBridge.FrameInfo frame)
        {
            currentFrame = frame;
            frameReady = frame.ViewsValid != 0 && frame.ShouldRender != 0;

            if (frame.Views != null && frame.Views.Length >= 2)
            {
                var a = frame.Views[0].Position;
                var b = frame.Views[1].Position;
                CenterEye = new System.Numerics.Vector3((a.X + b.X) * 0.5f, (a.Y + b.Y) * 0.5f, (a.Z + b.Z) * 0.5f);
                HeadViewValid = frame.ViewsValid != 0;
            }
            else
            {
                HeadViewValid = false;
            }

            // Log first frame for debugging
            if (loggedFirstFrame == false && frameReady)
            {
                VrCamera.LogViewInfo(0, frame.Views[0]);
                VrCamera.LogViewInfo(1, frame.Views[1]);
                loggedFirstFrame = true;
            }
        }

        /// <summary>
        /// Check if we should render in stereo this frame.
        /// </summary>
        public static bool ShouldRenderStereo()
        {
            return frameReady && VRManager.IsVRActive;
        }

        /// <summary>
        /// Get the VR camera matrix for a specific eye.
        /// Returns combined ViewProjection matrix for rendering.
        /// </summary>
        public static Matrix4x4 GetEyeViewProjection(int eyeIndex)
        {
            if (eyeIndex < 0 || eyeIndex > 1 || !frameReady)
                return Matrix4x4.Identity;

            return VrCamera.CreateVrViewProjection(
                currentFrame.Views[eyeIndex],
                dioramaWorld,
                nearZ: 0.1f,
                farZ: 1000f
            );
        }

        /// <summary>
        /// Apply VR camera transform to Barotrauma's camera object (reflection-based).
        /// Phase 2.1: For now, just log that we would set matrices.
        /// Phase 2.2: Actually modify Camera.ViewMatrix and Camera.ProjectionMatrix.
        /// </summary>
        public static void ApplyVrCameraTransform(object camera, int eyeIndex)
        {
            if (camera == null || cameraType == null)
                return;

            if (viewMatrixProperty == null || projectionMatrixProperty == null)
                return;

            var vpMatrix = GetEyeViewProjection(eyeIndex);

            // TODO Phase 2.2: Split VP matrix into separate View and Projection
            // For now, MonoGame might need the combined matrix differently

            // Placeholder: log that we're ready to set matrices
            if (applyTransformLogCount++ < 3)
            {
                VRManager.Log($"[VrStereoRenderer] Eye {eyeIndex} camera transform ready " +
                    $"(matrix setting TODO in Phase 2.2)");
            }
        }

        /// <summary>
        /// Render the game for one eye.
        /// Phase 2.1: Set up camera, trigger existing render passes.
        /// Phase 2.2: Capture to eye-specific render target.
        /// </summary>
        public static void RenderEye(int eyeIndex, object gameScreen, object spriteBatch)
        {
            if (!ShouldRenderStereo())
                return;

            // TODO Phase 2.2:
            // 1. Get/create render target for this eye
            // 2. Set as active render target
            // 3. Find Barotrauma's Camera instance
            // 4. ApplyVrCameraTransform(camera, eyeIndex)
            // 5. Call GameScreen's existing draw methods
            // 6. Restore original render target

            if (renderEyeLogCount++ < 3)
            {
                VRManager.Log($"[VrStereoRenderer] RenderEye({eyeIndex}) called - full impl in Phase 2.2");
            }
        }

        /// <summary>
        /// Called after both eyes are rendered - copy to VR swapchains.
        /// </summary>
        public static void SubmitStereoFrame()
        {
            if (!frameReady)
                return;

            // TODO Phase 2.2: Copy left/right eye render targets to XrBridge
            // For now, XrBridge still copies the backbuffer (Phase 1 behavior)
        }

        /// <summary>
        /// Configuration: adjust diorama positioning.
        /// </summary>
        public static void SetDioramaDistance(float metres)
        {
            dioramaWorld = VrCamera.CreateDioramaWorldMatrix(
                distanceMetres: metres,
                scale: 0.01f,
                rotation: 0f
            );
            VRManager.Log($"[VrStereoRenderer] Diorama distance set to {metres}m");
        }

        public static void SetDioramaScale(float scale)
        {
            dioramaWorld = VrCamera.CreateDioramaWorldMatrix(
                distanceMetres: 1.2f,
                scale: scale,
                rotation: 0f
            );
            VRManager.Log($"[VrStereoRenderer] Diorama scale set to {scale}");
        }
    }
}
