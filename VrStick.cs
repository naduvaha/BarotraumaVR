using System;
using System.Reflection;

namespace BarotraumaVR
{
    /// <summary>
    /// Left thumbstick stands in for the movement keys. Sideways walks,
    /// up and down climb or swim, and pressing the stick runs.
    /// </summary>
    public static class VrStick
    {
        private const float Deadzone = 0.4f;

        private static FieldInfo? inputTypeField;
        private static FieldInfo? heldField;
        private static FieldInfo? heldQueueField;
        private static FieldInfo? hitField;
        private static FieldInfo? hitQueueField;
        private static int up = -1, down = -1, left = -1, right = -1, run = -1, health = -1;
        private static int ragdoll = -1, crouch = -1, use = -1, drop = -1, deselect = -1;
        private static bool logged;
        private static bool bWasDown;
        private static bool healthHit;
        private static bool yWasDown;
        private static bool yHit;
        private static bool yDown;
        private static bool xWasDown;
        private static bool xHit;
        private static bool dropWasDown;
        private static bool dropHit;

        public static void Bind(Type keyType, Type inputType)
        {
            inputTypeField = keyType.GetField("inputType", BindingFlags.NonPublic | BindingFlags.Instance);
            heldField = keyType.GetField("held", BindingFlags.NonPublic | BindingFlags.Instance);
            heldQueueField = keyType.GetField("heldQueue", BindingFlags.NonPublic | BindingFlags.Instance);
            hitField = keyType.GetField("hit", BindingFlags.NonPublic | BindingFlags.Instance);
            hitQueueField = keyType.GetField("hitQueue", BindingFlags.NonPublic | BindingFlags.Instance);
            int Code(string name) => (int)Enum.Parse(inputType, name);
            up = Code("Up");
            down = Code("Down");
            left = Code("Left");
            right = Code("Right");
            run = Code("Run");
            health = Code("Health");
            ragdoll = Code("Ragdoll");
            crouch = Code("Crouch");
            use = Code("Use");
            drop = Code("DropItem");
            deselect = Code("Deselect");
        }

        public static void Sample()
        {
            if (!VRManager.IsVRActive)
                return;

            XrBridge.bvr_xr_get_input(out var input);
            var hands = input.Hands;
            if (hands == null || hands.Length < 1)
                return;

            bool bDown = (input.Buttons & 2) != 0;
            healthHit = bDown && !bWasDown;
            bWasDown = bDown;

            yDown = (input.Buttons & 8) != 0;
            yHit = yDown && !yWasDown;
            yWasDown = yDown;
            bool xButton = (input.Buttons & 4) != 0;
            xHit = xButton && !xWasDown;
            xWasDown = xButton;
            bool dropping = hands.Length > 1 && hands[1].StickClick != 0;
            dropHit = dropping && !dropWasDown;
            dropWasDown = dropping;

            float x = hands[0].StickX;
            float y = hands[0].StickY;
            bool moving = x < -Deadzone || x > Deadzone || y < -Deadzone || y > Deadzone;
            if (!logged && moving)
            {
                logged = true;
                VRManager.Log($"[BarotraumaVR] Left stick ({x:0.00}, {y:0.00}) moves the character");
            }
        }

        public static void ResetLog()
        {
            logged = false;
            bWasDown = false;
            healthHit = false;
            yWasDown = false;
            yHit = false;
            yDown = false;
            xWasDown = false;
            xHit = false;
            dropWasDown = false;
            dropHit = false;
        }

        public static void SetState_Postfix(object __instance)
        {
            if (!VRManager.IsVRActive || inputTypeField == null || heldField == null)
                return;

            int type = Convert.ToInt32(inputTypeField.GetValue(__instance));
            if (type == health && healthHit)
            {
                hitField?.SetValue(__instance, true);
                hitQueueField?.SetValue(__instance, true);
                heldField.SetValue(__instance, true);
                heldQueueField?.SetValue(__instance, true);
                VRManager.Log("[BarotraumaVR] B opens treatment");
                return;
            }
            if ((type == use && yHit) || (type == drop && dropHit) || (type == deselect && xHit))
            {
                hitField?.SetValue(__instance, true);
                hitQueueField?.SetValue(__instance, true);
            }
            if (!Held(type))
                return;

            heldField.SetValue(__instance, true);
            heldQueueField?.SetValue(__instance, true);
        }

        private static bool Held(int type)
        {
            XrBridge.bvr_xr_get_input(out var input);
            var hands = input.Hands;
            if (hands == null || hands.Length < 1)
                return false;

            float x = hands[0].StickX;
            float y = hands[0].StickY;
            if (type == left) return x < -Deadzone;
            if (type == right) return x > Deadzone;
            if (type == up) return y > Deadzone;
            if (type == down) return y < -Deadzone;
            if (type == run) return hands[0].StickClick != 0;
            if (type == ragdoll) return hands[0].Squeeze >= 0.55f;
            if (type == crouch) return hands[0].Trigger >= 0.55f;
            if (type == use) return yDown;
            return false;
        }
    }
}
