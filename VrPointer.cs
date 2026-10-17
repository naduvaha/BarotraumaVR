using System;
using System.Numerics;

namespace BarotraumaVR
{
    /// <summary>
    /// Right-controller aim ray against a plane in front of the headset.
    /// The hit is projected through the right eye and becomes a window pixel.
    /// The game image is copied 1:1 into the top-left of the eye texture, so the
    /// pixel lines up with that picture rather than the center of the view.
    /// </summary>
    public static class VrPointer
    {
        private const float PlaneDistance = 2f;
        private const float Pressed = 0.55f;

        public static bool HasPoint { get; private set; }
        public static int X { get; private set; }
        public static int Y { get; private set; }
        public static bool LeftDown { get; private set; }
        public static bool MiddleDown { get; private set; }
        public static bool RightDown { get; private set; }

        private static bool logged;

        public static void Update(XrBridge.FrameInfo frame)
        {
            var views = frame.Views;
            if (views == null || views.Length < 2)
                return;

            XrBridge.bvr_xr_get_input(out var input);
            var hands = input.Hands;
            if (hands == null || hands.Length < 2)
                return;

            int hand = hands[1].PoseValid != 0 ? 1 : hands[0].PoseValid != 0 ? 0 : -1;
            if (hand < 0)
                return;

            LeftDown = hands[hand].Trigger >= Pressed;
            MiddleDown = (input.Buttons & 1) != 0;
            RightDown = false;

            var eye = views[1];
            if (!TryHit(eye, hands[hand], out float pixelX, out float pixelY))
                return;

            X = (int)pixelX;
            Y = (int)pixelY;
            HasPoint = true;

            if (!logged)
            {
                logged = true;
                VRManager.Log($"[BarotraumaVR] Pointer {X}, {Y} from {(hand == 1 ? "right" : "left")} controller");
            }
        }

        public static void Reset()
        {
            HasPoint = false;
            logged = false;
        }

        private static bool TryHit(XrBridge.View eye, XrBridge.Hand hand, out float pixelX, out float pixelY)
        {
            pixelX = 0;
            pixelY = 0;

            var eyePos = new Vector3(eye.Position.X, eye.Position.Y, eye.Position.Z);
            var eyeRot = new Quaternion(eye.Orientation.X, eye.Orientation.Y, eye.Orientation.Z, eye.Orientation.W);
            var aimPos = new Vector3(hand.AimPosition.X, hand.AimPosition.Y, hand.AimPosition.Z);
            var aimRot = new Quaternion(hand.AimOrientation.X, hand.AimOrientation.Y, hand.AimOrientation.Z, hand.AimOrientation.W);

            var headForward = Vector3.Transform(new Vector3(0f, 0f, -1f), eyeRot);
            var aimForward = Vector3.Transform(new Vector3(0f, 0f, -1f), aimRot);
            float denom = Vector3.Dot(aimForward, headForward);
            if (MathF.Abs(denom) < 0.0001f)
                return false;

            var planePoint = eyePos + headForward * PlaneDistance;
            float t = Vector3.Dot(planePoint - aimPos, headForward) / denom;
            if (t < 0f)
                return false;

            var hit = aimPos + aimForward * t;
            var viewHit = Vector3.Transform(hit - eyePos, Quaternion.Inverse(eyeRot));
            if (viewHit.Z >= -0.05f)
                return false;

            float xTan = viewHit.X / -viewHit.Z;
            float yTan = viewHit.Y / -viewHit.Z;
            float tanL = MathF.Tan(eye.FieldOfView.Left);
            float tanR = MathF.Tan(eye.FieldOfView.Right);
            float tanU = MathF.Tan(eye.FieldOfView.Up);
            float tanD = MathF.Tan(eye.FieldOfView.Down);
            float spanX = tanR - tanL;
            float spanY = tanU - tanD;
            if (MathF.Abs(spanX) < 0.0001f || MathF.Abs(spanY) < 0.0001f)
                return false;

            float u = (xTan - tanL) / spanX;
            float v = (yTan - tanD) / spanY;

            int width = GamePixels("GraphicsWidth");
            int height = GamePixels("GraphicsHeight");
            if (width <= 0 || height <= 0)
                return false;

            XrBridge.bvr_xr_get_info(out var info);
            XrBridge.bvr_xr_get_picture_rect(out int rx, out int ry, out int rw, out int rh);
            if (info.RecommendedWidth <= 0 || info.RecommendedHeight <= 0 || rw <= 0 || rh <= 0)
                return false;

            float eyeX = u * info.RecommendedWidth;
            float eyeY = (1f - v) * info.RecommendedHeight;
            pixelX = (eyeX - rx) / rw * width;
            pixelY = (eyeY - ry) / rh * height;
            return true;
        }

        private static int GamePixels(string property)
        {
            var game = VRManager.FindType("Barotrauma.GameMain");
            var value = game?.GetProperty(property, System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
            return value is int n ? n : 0;
        }
    }
}
