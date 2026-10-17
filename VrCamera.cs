using System;
using System.Numerics;

namespace BarotraumaVR
{
    /// <summary>
    /// Converts OpenXR view data (FOV, pose) into perspective projection matrices
    /// for rendering the game from VR eye positions.
    ///
    /// Pattern from CasualtyVR: Keep game's logic camera untouched, create separate
    /// VR cameras that render the same scene from different perspectives.
    /// </summary>
    public class VrCamera
    {
        /// <summary>
        /// Creates a perspective projection matrix from OpenXR FOV (in radians).
        /// OpenXR convention: left/down are negative angles.
        /// </summary>
        public static Matrix4x4 CreatePerspectiveFov(XrBridge.Fov fov, float nearZ, float farZ)
        {
            // Convert OpenXR FOV (angles from center) to projection matrix
            // Based on XR_DOCS-000 pattern from CasualtyVR

            float tanLeft = MathF.Tan(fov.Left);
            float tanRight = MathF.Tan(fov.Right);
            float tanUp = MathF.Tan(fov.Up);
            float tanDown = MathF.Tan(fov.Down);

            float tanWidth = tanRight - tanLeft;
            float tanHeight = tanUp - tanDown;

            // Right-handed projection matrix for D3D11
            float m00 = 2.0f / tanWidth;
            float m11 = 2.0f / tanHeight;
            float m20 = (tanRight + tanLeft) / tanWidth;
            float m21 = (tanUp + tanDown) / tanHeight;
            float m22 = -(farZ + nearZ) / (farZ - nearZ);
            float m23 = -1.0f;
            float m32 = -(2.0f * farZ * nearZ) / (farZ - nearZ);

            return new Matrix4x4(
                m00, 0,   0,   0,
                0,   m11, 0,   0,
                m20, m21, m22, m23,
                0,   0,   m32, 0
            );
        }

        /// <summary>
        /// Creates a view matrix from OpenXR pose (position + orientation in LOCAL space).
        /// OpenXR uses right-handed coordinate system with -Z forward, +Y up, +X right.
        /// </summary>
        public static Matrix4x4 CreateViewMatrix(XrBridge.Vec3 position, XrBridge.Quat orientation)
        {
            // Convert OpenXR quaternion to Matrix4x4
            var quat = new Quaternion(orientation.X, orientation.Y, orientation.Z, orientation.W);
            var rotationMatrix = Matrix4x4.CreateFromQuaternion(quat);

            // Create translation
            var translationMatrix = Matrix4x4.CreateTranslation(position.X, position.Y, position.Z);

            // View matrix is inverse of world transform
            Matrix4x4.Invert(translationMatrix * rotationMatrix, out var viewMatrix);
            return viewMatrix;
        }

        /// <summary>
        /// Diorama world matrix: positions the 2D game plane as a "tabletop" scene
        /// in front of the seated player.
        ///
        /// Phase 2 default: Game plane at Z=-1.2m (1.2 meters in front of player),
        /// scaled to make submarine/world visible at comfortable size.
        /// </summary>
        public static Matrix4x4 CreateDioramaWorldMatrix(
            float distanceMetres = 1.2f,
            float scale = 0.01f,  // 1 game unit = 1cm (adjust based on Barotrauma's scale)
            float rotation = 0f)
        {
            // Scale: Barotrauma uses large world units, we scale down for VR viewing
            var scaleMatrix = Matrix4x4.CreateScale(scale);

            // Rotation: optional, for tilting the diorama
            var rotationMatrix = Matrix4x4.CreateRotationY(rotation);

            // Translation: move game world in front of player (-Z is forward in OpenXR)
            var translationMatrix = Matrix4x4.CreateTranslation(0, 0, -distanceMetres);

            return scaleMatrix * rotationMatrix * translationMatrix;
        }

        /// <summary>
        /// Combines view and projection matrices with diorama positioning.
        /// This is the final matrix used to render the game for one VR eye.
        /// </summary>
        public static Matrix4x4 CreateVrViewProjection(
            XrBridge.View view,
            Matrix4x4 dioramaWorld,
            float nearZ = 0.1f,
            float farZ = 1000f)
        {
            var projectionMatrix = CreatePerspectiveFov(view.FieldOfView, nearZ, farZ);
            var viewMatrix = CreateViewMatrix(view.Position, view.Orientation);

            // Final transform: Projection * View * DioramaWorld
            // Game vertices are in world space → diorama positions them → view transforms to eye space → projection
            return dioramaWorld * viewMatrix * projectionMatrix;
        }

        /// <summary>
        /// Diagnostic: logs camera parameters for debugging.
        /// </summary>
        public static void LogViewInfo(int eyeIndex, XrBridge.View view)
        {
            VRManager.Log($"[VrCamera] Eye {eyeIndex}: " +
                $"pos=({view.Position.X:F3}, {view.Position.Y:F3}, {view.Position.Z:F3}), " +
                $"fov=(L:{MathF.Abs(view.FieldOfView.Left) * 57.3f:F1}°, R:{view.FieldOfView.Right * 57.3f:F1}°, " +
                $"U:{view.FieldOfView.Up * 57.3f:F1}°, D:{MathF.Abs(view.FieldOfView.Down) * 57.3f:F1}°)");
        }
    }
}
