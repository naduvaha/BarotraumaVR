using System;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// Reads F10 from PlayerInput.Update. Lua's Hook.Patch cannot resolve this
    /// client type from the server script, so the patch is applied in C#.
    /// </summary>
    public static class InputPatches
    {
        private static bool applied;
        private static bool f10WasDown;
        private static bool f9WasDown;
        private static object? f9Key;
        private static bool loggedFailure;
        private static MethodInfo? keyDown;
        private static object? f10Key;
        private static FieldInfo? mouseStateField;
        private static FieldInfo? latestMouseField;
        private static ConstructorInfo? mouseCtor;
        private static object? buttonReleased;
        private static object? buttonPressed;
        private static bool loggedPointerError;
        private static FieldInfo? commandFrame;
        private static FieldInfo? commandOpening;
        private static MethodInfo? disableCommandUi;
        private static MethodInfo? keyHitInput;
        private static object? commandInput;

        public static void Apply(Harmony harmony)
        {
            if (applied)
                return;

            try
            {
                var playerInputType = FindType("Barotrauma.PlayerInput");
                var keysType = FindType("Microsoft.Xna.Framework.Input.Keys");
                if (playerInputType == null || keysType == null)
                {
                    if (!loggedFailure)
                    {
                        VRManager.Log("[BarotraumaVR] PlayerInput is not loaded yet; F10 hook will retry");
                        loggedFailure = true;
                    }
                    return;
                }

                var updateMethod = playerInputType.GetMethod("Update",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(double) },
                    null);
                keyDown = playerInputType.GetMethod("KeyDown",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { keysType },
                    null);
                if (updateMethod == null || keyDown == null)
                {
                    VRManager.Log("[BarotraumaVR] PlayerInput.Update/KeyDown was not found");
                    return;
                }

                f10Key = Enum.Parse(keysType, "F10");
                f9Key = Enum.Parse(keysType, "F9");
                BindMouse(playerInputType);
                BindStick(harmony);
                BindWindowActive(harmony);
                BindCommandToggle(harmony);
                var prefix = typeof(InputPatches).GetMethod(nameof(Update_Prefix), BindingFlags.Public | BindingFlags.Static);
                var postfix = typeof(InputPatches).GetMethod(nameof(Update_Postfix), BindingFlags.Public | BindingFlags.Static);
                harmony.Patch(updateMethod, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix));
                applied = true;
                VRManager.Log("[BarotraumaVR] F10 toggles VR, F9 toggles first person");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[BarotraumaVR] Failed to patch PlayerInput: {ex.Message}");
            }
        }

        public static void Update_Prefix()
        {
            WritePointer(latestOnly: true);
        }

        public static void Update_Postfix()
        {
            try
            {
                if (keyDown == null || f10Key == null)
                    return;

                var down = (bool)keyDown.Invoke(null, new[] { f10Key })!;
                if (down && !f10WasDown)
                {
                    VRManager.Log("[BarotraumaVR] F10 detected - toggling VR!");
                    VRManager.ToggleVR();
                }
                f10WasDown = down;

                if (f9Key != null)
                {
                    var f9 = (bool)keyDown.Invoke(null, new[] { f9Key })!;
                    if (f9 && !f9WasDown)
                    {
                        VRManager.Log("[BarotraumaVR] F9 detected - toggling first person");
                        VrFirstPerson.Toggle();
                    }
                    f9WasDown = f9;
                }

                WritePointer(latestOnly: false);
                VrStick.Sample();
            }
            catch (Exception ex)
            {
                if (!loggedFailure)
                {
                    VRManager.Log($"[BarotraumaVR] F10 check error: {ex.Message}");
                    loggedFailure = true;
                }
            }
        }

        private static void BindStick(Harmony harmony)
        {
            var keyType = FindType("Barotrauma.Key");
            var inputType = FindType("Barotrauma.InputType");
            var setState = keyType?.GetMethod("SetState", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);
            if (keyType == null || inputType == null || setState == null)
            {
                VRManager.Log("[BarotraumaVR] Movement keys were not found");
                return;
            }

            VrStick.Bind(keyType, inputType);
            var postfix = typeof(VrStick).GetMethod(nameof(VrStick.SetState_Postfix), BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(setState, postfix: new HarmonyMethod(postfix));
            VRManager.Log("[BarotraumaVR] Left grip holds ragdoll, left trigger crouches, Y uses, X lets go, right stick click drops");
        }

        private static void BindCommandToggle(Harmony harmony)
        {
            var crew = FindType("Barotrauma.CrewManager");
            var inputType = FindType("Barotrauma.InputType");
            var playerInput = FindType("Barotrauma.PlayerInput");
            if (crew == null || inputType == null || playerInput == null)
                return;

            commandFrame = crew.GetField("commandFrame", BindingFlags.NonPublic | BindingFlags.Instance);
            commandOpening = crew.GetField("isOpeningClick", BindingFlags.NonPublic | BindingFlags.Instance);
            disableCommandUi = crew.GetMethod("DisableCommandUI", BindingFlags.Public | BindingFlags.Instance);
            commandInput = Enum.Parse(inputType, "Command");
            keyHitInput = playerInput.GetMethod("KeyHit", BindingFlags.Public | BindingFlags.Static, null, new[] { inputType }, null);

            var create = crew.GetMethod("CreateCommandUI", BindingFlags.NonPublic | BindingFlags.Instance);
            var update = crew.GetMethod("Update", BindingFlags.Public | BindingFlags.Instance, null, new[] { typeof(float) }, null);
            if (create != null)
                harmony.Patch(create, prefix: new HarmonyMethod(typeof(InputPatches).GetMethod(nameof(CreateCommandUI_Prefix), BindingFlags.Public | BindingFlags.Static)));
            if (update != null)
                harmony.Patch(update, postfix: new HarmonyMethod(typeof(InputPatches).GetMethod(nameof(CrewUpdate_Postfix), BindingFlags.Public | BindingFlags.Static)));
            VRManager.Log("[BarotraumaVR] Command key toggles the order menu off");
        }

        public static bool CreateCommandUI_Prefix(object __instance)
        {
            if (!VRManager.IsVRActive || commandFrame == null || disableCommandUi == null)
                return true;
            if (commandFrame.GetValue(__instance) == null)
                return true;
            disableCommandUi.Invoke(__instance, null);
            return false;
        }

        public static void CrewUpdate_Postfix(object __instance)
        {
            if (!VRManager.IsVRActive || commandFrame == null || disableCommandUi == null || keyHitInput == null)
                return;
            if (commandFrame.GetValue(__instance) == null)
                return;
            if (commandOpening?.GetValue(__instance) is true)
                return;
            if (keyHitInput.Invoke(null, new[] { commandInput }) is true)
                disableCommandUi.Invoke(__instance, null);
        }

        private static void BindWindowActive(Harmony harmony)
        {
            var getter = FindType("Barotrauma.GameMain")?.GetProperty("WindowActive", BindingFlags.Public | BindingFlags.Static)?.GetGetMethod();
            if (getter == null)
            {
                VRManager.Log("[BarotraumaVR] GameMain.WindowActive was not found");
                return;
            }

            var postfix = typeof(InputPatches).GetMethod(nameof(WindowActive_Postfix), BindingFlags.Public | BindingFlags.Static);
            harmony.Patch(getter, postfix: new HarmonyMethod(postfix));
            VRManager.Log("[BarotraumaVR] VR keeps input when the game window is not focused");
        }

        public static void WindowActive_Postfix(ref bool __result)
        {
            if (VRManager.IsVRActive)
                __result = true;
        }

        private static void BindMouse(Type playerInputType)
        {
            mouseStateField = playerInputType.GetField("mouseState", BindingFlags.NonPublic | BindingFlags.Static);
            latestMouseField = playerInputType.GetField("latestMouseState", BindingFlags.NonPublic | BindingFlags.Static);
            var mouseType = mouseStateField?.FieldType;
            var buttonType = FindType("Microsoft.Xna.Framework.Input.ButtonState");
            if (mouseType == null || buttonType == null)
            {
                VRManager.Log("[BarotraumaVR] Mouse state fields were not found");
                return;
            }

            buttonReleased = Enum.ToObject(buttonType, 0);
            buttonPressed = Enum.ToObject(buttonType, 1);
            foreach (var ctor in mouseType.GetConstructors())
            {
                var p = ctor.GetParameters();
                if (p.Length >= 8 && p[0].ParameterType == typeof(int) && p[3].ParameterType == buttonType)
                {
                    mouseCtor = ctor;
                    break;
                }
            }

            if (mouseCtor == null)
                VRManager.Log("[BarotraumaVR] MouseState constructor was not found");
        }

        private static void WritePointer(bool latestOnly)
        {
            if (!VRManager.IsVRActive || !VrPointer.HasPoint || mouseCtor == null || latestMouseField == null)
                return;

            try
            {
                int scroll = 0;
                var current = (latestOnly ? latestMouseField.GetValue(null) : mouseStateField?.GetValue(null))
                    ?? latestMouseField.GetValue(null);
                var scrollProp = current?.GetType().GetProperty("ScrollWheelValue");
                if (scrollProp != null && current != null)
                    scroll = (int)scrollProp.GetValue(current)!;

                bool grip = false;
                bool trigger = false;
                XrBridge.bvr_xr_get_input(out var input);
                if (input.Hands != null && input.Hands.Length > 1)
                {
                    grip = input.Hands[1].Squeeze >= 0.55f;
                    trigger = input.Hands[1].Trigger >= 0.55f;
                }
                var left = (trigger || grip || VrPointer.LeftDown) ? buttonPressed : buttonReleased;
                var middle = VrPointer.MiddleDown ? buttonPressed : buttonReleased;
                var right = VrPointer.RightDown ? buttonPressed : buttonReleased;
                var args = mouseCtor.GetParameters().Length == 8
                    ? new[] { (object)VrPointer.X, VrPointer.Y, scroll, left!, middle!, right!, buttonReleased!, buttonReleased! }
                    : new[] { (object)VrPointer.X, VrPointer.Y, scroll, left!, middle!, right!, buttonReleased!, buttonReleased!, 0 };
                var state = mouseCtor.Invoke(args);
                latestMouseField.SetValue(null, state);
                if (!latestOnly)
                    mouseStateField?.SetValue(null, state);
            }
            catch (Exception ex)
            {
                if (!loggedPointerError)
                {
                    VRManager.Log($"[BarotraumaVR] Pointer write failed: {ex.Message}");
                    loggedPointerError = true;
                }
            }
        }

        private static Type? FindType(string fullName)
        {
            var found = Type.GetType(fullName + ", Barotrauma", throwOnError: false);
            if (found != null)
                return found;

            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                found = assembly.GetType(fullName, throwOnError: false);
                if (found != null)
                    return found;
            }

            return null;
        }
    }
}
