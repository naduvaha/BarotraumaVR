using System;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// A worn diving suit sets LightManager.ObstructVisionAmount. Vanilla then
    /// clears LosTexture to black and punches a mouse-aimed hole. In VR that
    /// hole is off-center, so LOS paints the cave background over the sub and
    /// one eye loses the character. Zero the visor amount (it is a field, not
    /// a property) and only unbind shader views so LosTexture can be an RT again.
    /// </summary>
    internal static class VrSuitVision
    {
        private static Harmony? pendingHarmony;
        private static bool applied;
        private static bool logged;
        private static FieldInfo? obstructField;
        private static PropertyInfo? obstructProp;
        private static PropertyInfo? texturesProp;
        private static PropertyInfo? textureItem;
        private static MethodInfo? setRenderTarget;
        private static int missingLogs;

        public static void Retry()
        {
            if (!applied && pendingHarmony != null)
                Apply(pendingHarmony);
        }

        public static void Apply(Harmony harmony)
        {
            pendingHarmony = harmony;
            if (applied)
                return;

            try
            {
                var type = Find("Barotrauma.Lights.LightManager");
                if (type == null)
                {
                    if (missingLogs++ < 3)
                        VRManager.Log("[VrSuitVision] LightManager is not loaded yet");
                    return;
                }

                MethodInfo? update = null;
                foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (method.Name == "UpdateObstructVision" && method.GetParameters().Length == 4)
                    {
                        update = method;
                        break;
                    }
                }
                if (update == null)
                {
                    VRManager.Log("[VrSuitVision] UpdateObstructVision was not found");
                    return;
                }

                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                obstructField = type.GetField("ObstructVisionAmount", flags);
                obstructProp = type.GetProperty("ObstructVisionAmount", flags);
                if (obstructField == null && obstructProp == null)
                {
                    VRManager.Log("[VrSuitVision] ObstructVisionAmount was not found");
                    return;
                }

                harmony.Patch(update, prefix: new HarmonyMethod(typeof(VrSuitVision).GetMethod(nameof(HideHelmetVignette), BindingFlags.Public | BindingFlags.Static)));
                applied = true;
                VRManager.Log("[VrSuitVision] Diving helmet visor uses the field, not the LOS hole");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrSuitVision] Patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideHelmetVignette(object __instance, object graphics)
        {
            if (!VRManager.IsVRActive || __instance == null)
                return;

            try
            {
                float amount = ReadAmount(__instance);
                if (amount > 0.01f)
                    WriteAmount(__instance, 0f);

                UnbindShaderResources(graphics);

                if (!logged && amount > 0.01f)
                {
                    logged = true;
                    VRManager.Log("[VrSuitVision] Suit visor skipped so both eyes keep the character");
                }
            }
            catch (Exception ex)
            {
                if (!logged)
                {
                    logged = true;
                    VRManager.Log($"[VrSuitVision] Visor skip failed: {ex.Message}");
                }
            }
        }

        public static void PrepareCopy(object? graphics)
        {
            SetBackbuffer(graphics);
            UnbindShaderResources(graphics);
        }

        private static float ReadAmount(object instance)
        {
            object? current = obstructField?.GetValue(instance) ?? obstructProp?.GetValue(instance);
            return current is float f ? f : 0f;
        }

        private static void WriteAmount(object instance, float value)
        {
            if (obstructField != null)
                obstructField.SetValue(instance, value);
            else
                obstructProp?.SetValue(instance, value);
        }

        private static void SetBackbuffer(object? graphics)
        {
            if (graphics == null)
                return;
            try
            {
                setRenderTarget ??= FindSetRenderTarget(graphics.GetType());
                setRenderTarget?.Invoke(graphics, new object?[] { null });
            }
            catch
            {
            }
        }

        private static void UnbindShaderResources(object? graphics)
        {
            if (graphics == null)
                return;
            try
            {
                texturesProp ??= graphics.GetType().GetProperty("Textures");
                object? textures = texturesProp?.GetValue(graphics);
                if (textures == null)
                    return;
                textureItem ??= textures.GetType().GetProperty("Item");
                if (textureItem == null)
                    return;
                int slots = 4;
                object? count = textures.GetType().GetProperty("Count")?.GetValue(textures)
                    ?? textures.GetType().GetProperty("Length")?.GetValue(textures);
                if (count is int n && n > 0)
                    slots = Math.Min(n, 8);
                for (int i = 0; i < slots; i++)
                    textureItem.SetValue(textures, null, new object[] { i });
            }
            catch
            {
            }
        }

        private static MethodInfo? FindSetRenderTarget(Type graphicsType)
        {
            foreach (var method in graphicsType.GetMethods(BindingFlags.Public | BindingFlags.Instance))
            {
                if (method.Name != "SetRenderTarget")
                    continue;
                var args = method.GetParameters();
                if (args.Length == 1 && args[0].ParameterType.Name == "RenderTarget2D")
                    return method;
            }
            return null;
        }

        private static Type? Find(string fullName)
        {
            return Type.GetType(fullName + ", Barotrauma", throwOnError: false) ?? VRManager.FindType(fullName);
        }
    }
}
