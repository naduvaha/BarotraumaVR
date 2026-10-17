using System;
using System.Collections;
using System.Numerics;
using System.Reflection;
using HarmonyLib;

namespace BarotraumaVR
{
    /// <summary>
    /// Phase 5 first person: keep the working 2D stereo diorama, but zoom the
    /// ortho camera to the room the character is standing in. HUD stays hidden
    /// until A is pressed.
    /// </summary>
    internal static class VrFirstPerson
    {
        private const uint ButtonA = 1;
        private const float LocalWidth = 720f;
        private const float LocalHeight = 420f;

        private static Harmony? pendingHarmony;
        private static bool applied;
        private static bool wanted;
        private static bool loggedOn;
        private static bool loggedHud;
        private static bool hudWanted;
        private static bool aWasDown;
        private static int missingLogs;
        private static PropertyInfo? controlledProp;
        private static FieldInfo? animField;
        private static PropertyInfo? dirProp;
        private static PropertyInfo? worldPosProp;
        private static MethodInfo? getLimb;
        private static object? headLimb;
        private static Type? matrixType;
        private static ConstructorInfo? matrixCtor;
        private static float headX;
        private static float headY;
        private static float facing = 1f;
        private static bool hasHead;
        private static FieldInfo? vertexTL;
        private static FieldInfo? vertexTR;
        private static FieldInfo? vertexBL;
        private static FieldInfo? vertexBR;
        private static FieldInfo? posField;
        private static FieldInfo? xField;
        private static FieldInfo? zField;
        private static FieldInfo? hudField;
        private static FieldInfo? namesField;
        private static bool hudForced;
        private static bool savedHud;
        private static bool savedNames;
        private static object? pinnedCam;
        private static float savedZoom = 1f;
        private static float savedMaxZoom = 2f;
        private static float savedOffset;
        private static bool cameraPinned;

        public static bool Wanted => wanted;
        public static bool Active => wanted && VRManager.IsVRActive && hasHead && Controlled() != null;

        public static bool HudVisible
        {
            get
            {
                if (!Active)
                    return true;
                if (hudWanted)
                    return true;
                return UiForcesHud();
            }
        }

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
                var character = Find("Barotrauma.Character");
                var limb = Find("Barotrauma.Limb");
                var session = Find("Barotrauma.GameSession");
                var screen = Find("Barotrauma.Screen");
                if (character == null)
                {
                    if (missingLogs++ < 3)
                        VRManager.Log("[VrFirstPerson] Character is not loaded yet");
                    return;
                }

                controlledProp = character.GetProperty("Controlled", BindingFlags.Public | BindingFlags.Static);
                animField = character.GetField("AnimController", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                var animType = animField?.FieldType ?? Find("Barotrauma.AnimController");
                dirProp = animType?.GetProperty("Dir");
                worldPosProp = limb?.GetProperty("WorldPosition") ?? character.GetProperty("WorldPosition");
                var limbType = Find("Barotrauma.LimbType");
                var ragdoll = Find("Barotrauma.Ragdoll") ?? animType?.BaseType;
                if (ragdoll != null && limbType != null)
                {
                    foreach (var method in ragdoll.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (method.Name != "GetLimb")
                            continue;
                        var args = method.GetParameters();
                        if (args.Length >= 1 && args[0].ParameterType == limbType)
                        {
                            getLimb = method;
                            break;
                        }
                    }
                    headLimb = Enum.Parse(limbType, "Head");
                }

                var hideBody = typeof(VrFirstPerson).GetMethod(nameof(HideOwnBody), BindingFlags.Public | BindingFlags.Static);
                var draw = character.GetMethod("Draw", BindingFlags.Public | BindingFlags.Instance);
                var drawFront = character.GetMethod("DrawFront", BindingFlags.Public | BindingFlags.Instance);
                if (draw != null)
                    harmony.Patch(draw, prefix: new HarmonyMethod(hideBody));
                var hideFront = typeof(VrFirstPerson).GetMethod(nameof(HideInteractionFront), BindingFlags.Public | BindingFlags.Static);
                if (drawFront != null)
                    harmony.Patch(drawFront, prefix: new HarmonyMethod(hideFront));
                var hideLimb = typeof(VrFirstPerson).GetMethod(nameof(HideOwnLimb), BindingFlags.Public | BindingFlags.Static);
                var limbChar = limb?.GetField("character", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
                LimbCharacter = limbChar;
                if (limb != null && hideLimb != null)
                {
                    foreach (var method in limb.GetMethods(BindingFlags.Public | BindingFlags.Instance))
                    {
                        if (method.Name == "Draw" && !method.IsAbstract)
                            harmony.Patch(method, prefix: new HarmonyMethod(hideLimb));
                    }
                }

                var endRound = session?.GetMethod("EndRound", BindingFlags.Public | BindingFlags.Instance);
                if (endRound != null)
                    harmony.Patch(endRound, prefix: new HarmonyMethod(typeof(VrFirstPerson).GetMethod(nameof(EndRound_Prefix), BindingFlags.Public | BindingFlags.Static)));

                var select = screen?.GetMethod("Select", BindingFlags.Public | BindingFlags.Instance);
                if (select != null)
                    harmony.Patch(select, postfix: new HarmonyMethod(typeof(VrFirstPerson).GetMethod(nameof(ScreenSelect_Postfix), BindingFlags.Public | BindingFlags.Static)));

                applied = true;
                VRManager.Log("[VrFirstPerson] F9 zooms the 2D view to the character's room. A shows the vanilla HUD");
            }
            catch (Exception ex)
            {
                VRManager.Log($"[VrFirstPerson] Patch failed: {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static FieldInfo? LimbCharacter;

        public static void Toggle()
        {
            if (!VRManager.IsVRActive)
            {
                VRManager.Log("[VrFirstPerson] Turn VR on with F10 before switching view");
                return;
            }

            wanted = !wanted;
            loggedOn = false;
            loggedHud = false;
            hudWanted = false;
            aWasDown = false;
            if (!wanted)
            {
                RestoreGameHud();
                RestoreLocalCamera();
                VRManager.Log("[VrFirstPerson] 2D diorama");
            }
            else
                VRManager.Log("[VrFirstPerson] First person: 2D view of the character's room. Press A for the vanilla HUD");
        }

        public static void OnVrDisabled()
        {
            RestoreGameHud();
            RestoreLocalCamera();
            wanted = false;
            hasHead = false;
            loggedOn = false;
            loggedHud = false;
            hudWanted = false;
            aWasDown = false;
        }

        public static void EndRound_Prefix()
        {
            if (!VRManager.IsVRActive)
                return;
            VRManager.Log("[BarotraumaVR] Round ended, leaving VR");
            VRManager.DisableVR();
        }

        public static void ScreenSelect_Postfix(object __instance)
        {
            if (!VRManager.IsVRActive || __instance == null)
                return;
            string name = __instance.GetType().Name;
            if (name is "MainMenuScreen" or "NetLobbyScreen" or "RoundSummaryScreen" or "SubEditorScreen")
            {
                VRManager.Log($"[BarotraumaVR] {name} selected, leaving VR");
                VRManager.DisableVR();
            }
        }

        public static void PrepareFrame()
        {
            hasHead = false;
            if (!wanted || !VRManager.IsVRActive)
            {
                RestoreLocalCamera();
                return;
            }
            try
            {
                object? character = Controlled();
                if (character == null)
                    return;
                object? anim = animField?.GetValue(character);
                facing = 1f;
                if (dirProp != null && anim != null)
                    facing = Convert.ToSingle(dirProp.GetValue(anim));
                if (Math.Abs(facing) < 0.1f)
                    facing = 1f;

                object? pos = HeadWorld(anim) ?? character.GetType().GetProperty("WorldPosition")?.GetValue(character);
                if (pos == null)
                    return;
                headX = ReadAxis(pos, "X");
                headY = ReadAxis(pos, "Y");
                hasHead = true;

                FitLocalCamera(character, anim);
                SampleHudButton();

                if (!loggedOn)
                {
                    loggedOn = true;
                    VRManager.Log("[VrFirstPerson] 2D view limited to the character's room");
                }
            }
            catch (Exception ex)
            {
                if (!loggedOn)
                {
                    loggedOn = true;
                    VRManager.Log($"[VrFirstPerson] Head sample failed: {ex.Message}");
                }
            }
        }

        public static object? EyeMatrix(int eye, Type xnaMatrix)
        {
            return null;
        }

        /// <summary>
        /// Stand each sprite in YZ at its centre X so a camera looking along the
        /// hull sees the art face-on, not the thin edge of the XY quad.
        /// </summary>
        public static void BillboardSprite(object item)
        {
            if (!Active || vertexTL == null || posField == null || xField == null || zField == null)
                return;

            object tl = vertexTL.GetValue(item)!;
            object tr = vertexTR!.GetValue(item)!;
            object bl = vertexBL!.GetValue(item)!;
            object br = vertexBR!.GetValue(item)!;
            float x0 = ReadPosX(tl);
            float x1 = ReadPosX(tr);
            float x2 = ReadPosX(bl);
            float x3 = ReadPosX(br);
            float cx = (x0 + x1 + x2 + x3) * 0.25f;
            float sign = facing >= 0f ? 1f : -1f;
            WriteBillboard(tl, cx, sign);
            WriteBillboard(tr, cx, sign);
            WriteBillboard(bl, cx, sign);
            WriteBillboard(br, cx, sign);
            vertexTL.SetValue(item, tl);
            vertexTR.SetValue(item, tr);
            vertexBL.SetValue(item, bl);
            vertexBR.SetValue(item, br);
        }

        public static void BindBillboard(Type itemType)
        {
            vertexTL = itemType.GetField("vertexTL", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            vertexTR = itemType.GetField("vertexTR", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            vertexBL = itemType.GetField("vertexBL", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            vertexBR = itemType.GetField("vertexBR", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var vertexType = vertexTL?.FieldType;
            posField = vertexType?.GetField("Position", BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            var vecType = posField?.FieldType;
            xField = vecType?.GetField("X");
            zField = vecType?.GetField("Z");
        }

        /// <summary>
        /// Put the game cursor on a world point ahead of the captain so Use hits
        /// doors in front instead of a pixel on the old 2D picture.
        /// </summary>
        public static bool TryAimScreen(out int x, out int y)
        {
            x = 0;
            y = 0;
            if (!Active)
                return false;

            try
            {
                var screen = Find("Barotrauma.Screen")?.GetProperty("Selected", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                var cam = screen?.GetType().GetProperty("Cam")?.GetValue(screen);
                var worldToScreen = cam?.GetType().GetMethod("WorldToScreen");
                if (cam == null || worldToScreen == null)
                    return false;

                float aimX = headX + facing * 40f;
                float aimY = headY - 24f;
                XrBridge.bvr_xr_get_input(out var input);
                if (input.Hands != null && input.Hands.Length > 1 && input.Hands[1].PoseValid != 0)
                {
                    var aim = input.Hands[1];
                    var rot = new Quaternion(aim.AimOrientation.X, aim.AimOrientation.Y, aim.AimOrientation.Z, aim.AimOrientation.W);
                    var fwd = Vector3.Transform(-Vector3.UnitZ, rot);
                    aimY += fwd.Y * 80f;
                }

                var vecType = worldToScreen.GetParameters()[0].ParameterType;
                object world = vecType.GetConstructor(new[] { typeof(float), typeof(float) }) is ConstructorInfo ctor
                    ? ctor.Invoke(new object[] { aimX, aimY })
                    : MakeVec(vecType, aimX, aimY);
                object? screenPos = worldToScreen.Invoke(cam, new[] { world });
                if (screenPos == null)
                    return false;
                x = (int)ReadAxis(screenPos, "X");
                y = (int)ReadAxis(screenPos, "Y");
                return true;
            }
            catch
            {
                return false;
            }
        }

        public static bool HideOwnBody(object __instance)
        {
            return true;
        }

        private static void FitLocalCamera(object character, object? anim)
        {
            object? screen = Find("Barotrauma.Screen")?.GetProperty("Selected", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            object? cam = screen?.GetType().GetProperty("Cam")?.GetValue(screen);
            if (cam == null)
                return;

            int w = GamePixels("GraphicsWidth");
            int h = GamePixels("GraphicsHeight");
            if (w <= 0 || h <= 0)
                return;

            float areaW = LocalWidth;
            float areaH = LocalHeight;
            object? hull = character.GetType().GetField("CurrentHull")?.GetValue(character)
                ?? anim?.GetType().GetProperty("CurrentHull")?.GetValue(anim);
            object? rect = hull?.GetType().GetProperty("WorldRect")?.GetValue(hull);
            if (rect != null)
            {
                float hullW = ReadAxis(rect, "Width");
                float hullH = ReadAxis(rect, "Height");
                if (hullW > 48f && hullH > 48f)
                {
                    areaW = Math.Min(hullW + 48f, LocalWidth);
                    areaH = Math.Min(hullH + 48f, LocalHeight);
                }
            }

            float zoom = Math.Min(w / areaW, h / areaH);
            zoom = Math.Clamp(zoom, 0.8f, 6f);

            var camType = cam.GetType();
            if (!cameraPinned)
            {
                savedZoom = Convert.ToSingle(camType.GetProperty("Zoom")?.GetValue(cam) ?? 1f);
                savedMaxZoom = Convert.ToSingle(camType.GetProperty("MaxZoom")?.GetValue(cam) ?? 2f);
                savedOffset = Convert.ToSingle(camType.GetProperty("OffsetAmount")?.GetValue(cam) ?? 0f);
                pinnedCam = cam;
                cameraPinned = true;
            }

            camType.GetProperty("MaxZoom")?.SetValue(cam, Math.Max(savedMaxZoom, 8f));
            camType.GetProperty("Zoom")?.SetValue(cam, zoom);
            camType.GetProperty("OffsetAmount")?.SetValue(cam, 0f);
            camType.GetField("targetZoom", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(cam, zoom);

            PropertyInfo? posProp = camType.GetProperty("Position");
            object? current = posProp?.GetValue(cam);
            if (current != null && posProp != null)
            {
                var vecType = current.GetType();
                object world = vecType.GetConstructor(new[] { typeof(float), typeof(float) }) is ConstructorInfo ctor
                    ? ctor.Invoke(new object[] { headX, headY })
                    : MakeVec(vecType, headX, headY);
                posProp.SetValue(cam, world);
            }

            camType.GetMethod("UpdateTransform")?.Invoke(cam, new object[] { false, true });
        }

        private static void RestoreLocalCamera()
        {
            if (!cameraPinned)
                return;
            try
            {
                object? cam = pinnedCam;
                if (cam != null)
                {
                    var camType = cam.GetType();
                    camType.GetProperty("MaxZoom")?.SetValue(cam, savedMaxZoom);
                    camType.GetProperty("Zoom")?.SetValue(cam, savedZoom);
                    camType.GetProperty("OffsetAmount")?.SetValue(cam, savedOffset);
                    camType.GetField("targetZoom", BindingFlags.NonPublic | BindingFlags.Instance)?.SetValue(cam, savedZoom);
                    camType.GetMethod("UpdateTransform")?.Invoke(cam, new object[] { false, true });
                }
            }
            catch
            {
            }
            cameraPinned = false;
            pinnedCam = null;
        }

        public static bool HideInteractionFront(object __instance)
        {
            if (!Active)
                return true;
            if (!HudVisible)
                return false;
            return HideOwnBody(__instance);
        }

        public static void HideGameHud()
        {
            if (hudForced || !Active || HudVisible)
                return;
            try
            {
                var gui = Find("Barotrauma.GUI");
                hudField ??= gui?.GetField("DisableHUD", BindingFlags.Public | BindingFlags.Static);
                namesField ??= gui?.GetField("DisableCharacterNames", BindingFlags.Public | BindingFlags.Static);
                if (hudField == null)
                    return;
                savedHud = hudField.GetValue(null) is true;
                savedNames = namesField?.GetValue(null) is true;
                hudField.SetValue(null, true);
                namesField?.SetValue(null, true);
                hudForced = true;
            }
            catch
            {
            }
        }

        public static void RestoreGameHud()
        {
            if (!hudForced)
                return;
            try
            {
                hudField?.SetValue(null, savedHud);
                namesField?.SetValue(null, savedNames);
            }
            catch
            {
            }
            hudForced = false;
        }

        public static bool HideOwnLimb(object __instance)
        {
            return true;
        }

        private static object? HeadWorld(object? anim)
        {
            if (anim == null || getLimb == null || headLimb == null)
                return null;
            object? limb;
            var args = getLimb.GetParameters();
            if (args.Length == 1)
                limb = getLimb.Invoke(anim, new[] { headLimb });
            else
            {
                var call = new object?[args.Length];
                call[0] = headLimb;
                for (int i = 1; i < args.Length; i++)
                {
                    if (args[i].ParameterType == typeof(bool))
                        call[i] = args[i].HasDefaultValue ? args[i].DefaultValue : true;
                    else if (args[i].HasDefaultValue)
                        call[i] = args[i].DefaultValue;
                }
                limb = getLimb.Invoke(anim, call);
            }
            return worldPosProp?.GetValue(limb);
        }

        private static float ReadAxis(object pos, string name)
        {
            return Convert.ToSingle(pos.GetType().GetField(name)?.GetValue(pos)
                ?? pos.GetType().GetProperty(name)?.GetValue(pos));
        }

        private static float ReadPosX(object vertex)
        {
            object pos = posField!.GetValue(vertex)!;
            return Convert.ToSingle(xField!.GetValue(pos));
        }

        private static void WriteBillboard(object vertex, float cx, float sign)
        {
            object pos = posField!.GetValue(vertex)!;
            float x = Convert.ToSingle(xField!.GetValue(pos));
            xField.SetValue(pos, cx);
            zField!.SetValue(pos, sign * (x - cx));
            posField.SetValue(vertex, pos);
        }

        private static object MakeVec(Type vecType, float x, float y)
        {
            var box = Activator.CreateInstance(vecType)!;
            vecType.GetField("X")?.SetValue(box, x);
            vecType.GetField("Y")?.SetValue(box, y);
            vecType.GetProperty("X")?.SetValue(box, x);
            vecType.GetProperty("Y")?.SetValue(box, y);
            return box;
        }

        private static object BoxMatrix(Matrix4x4 m, Type xnaMatrix)
        {
            matrixType ??= xnaMatrix;
            matrixCtor ??= xnaMatrix.GetConstructor(new[]
            {
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float),
                typeof(float), typeof(float), typeof(float), typeof(float)
            });
            if (matrixCtor != null)
            {
                return matrixCtor.Invoke(new object[]
                {
                    m.M11, m.M12, m.M13, m.M14,
                    m.M21, m.M22, m.M23, m.M24,
                    m.M31, m.M32, m.M33, m.M34,
                    m.M41, m.M42, m.M43, m.M44
                });
            }

            var box = Activator.CreateInstance(xnaMatrix)!;
            void Set(string name, float value) => xnaMatrix.GetField(name)?.SetValue(box, value);
            Set("M11", m.M11); Set("M12", m.M12); Set("M13", m.M13); Set("M14", m.M14);
            Set("M21", m.M21); Set("M22", m.M22); Set("M23", m.M23); Set("M24", m.M24);
            Set("M31", m.M31); Set("M32", m.M32); Set("M33", m.M33); Set("M34", m.M34);
            Set("M41", m.M41); Set("M42", m.M42); Set("M43", m.M43); Set("M44", m.M44);
            return box;
        }

        private static object? Controlled() => controlledProp?.GetValue(null);

        private static void SampleHudButton()
        {
            XrBridge.bvr_xr_get_input(out var input);
            bool aDown = (input.Buttons & ButtonA) != 0;
            if (aDown && !aWasDown)
            {
                hudWanted = !hudWanted;
                if (!loggedHud)
                {
                    loggedHud = true;
                    VRManager.Log("[VrFirstPerson] A toggles the vanilla HUD");
                }
            }
            aWasDown = aDown;
        }

        private static bool UiForcesHud()
        {
            try
            {
                var gui = Find("Barotrauma.GUI");
                if (gui?.GetProperty("PauseMenuOpen")?.GetValue(null) is true)
                    return true;
                if (gui?.GetProperty("SettingsMenuOpen")?.GetValue(null) is true)
                    return true;
                if (gui?.GetProperty("InputBlockingMenuOpen")?.GetValue(null) is true)
                    return true;

                var health = Find("Barotrauma.CharacterHealth");
                if (health?.GetProperty("OpenHealthWindow", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) != null)
                    return true;

                var crew = Find("Barotrauma.CrewManager");
                if (crew?.GetProperty("IsCommandInterfaceOpen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true)
                    return true;

                var talk = Find("Barotrauma.ConversationAction");
                if (talk?.GetProperty("IsDialogOpen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true)
                    return true;

                var hud = Find("Barotrauma.CharacterHUD");
                if (hud?.GetProperty("IsCampaignInterfaceOpen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true)
                    return true;

                var session = Find("Barotrauma.GameSession");
                if (session?.GetProperty("IsTabMenuOpen", BindingFlags.Public | BindingFlags.Static)?.GetValue(null) is true)
                    return true;

                object? character = Controlled();
                if (character == null)
                    return false;

                var type = character.GetType();
                if (type.GetProperty("SelectedCharacter")?.GetValue(character) != null)
                    return true;
                if (HasInterface(type.GetProperty("SelectedItem")?.GetValue(character)))
                    return true;

                object? secondary = type.GetProperty("SelectedSecondaryItem")?.GetValue(character);
                if (HasInterface(secondary) && !ItemHasComponent(secondary, "Ladder"))
                    return true;
            }
            catch
            {
            }
            return false;
        }

        private static bool HasInterface(object? item)
        {
            if (item == null)
                return false;
            var type = item.GetType();
            if (ItemHasComponent(item, "Ladder") || ItemHasComponent(item, "Door"))
                return false;
            if (type.GetProperty("OwnInventory")?.GetValue(item) != null)
                return true;
            if (HasAny(type.GetProperty("ActiveHUDs")?.GetValue(item)))
                return true;
            object? components = type.GetProperty("Components")?.GetValue(item);
            if (components is IEnumerable list)
            {
                foreach (var component in list)
                {
                    if (component == null)
                        continue;
                    var ct = component.GetType();
                    if (ct.GetProperty("GuiFrame")?.GetValue(component) != null)
                        return true;
                    if (ct.GetProperty("DrawHudWhenEquipped")?.GetValue(component) is true)
                        return true;
                }
            }
            return false;
        }

        private static bool ItemHasComponent(object? item, string name)
        {
            if (item == null)
                return false;
            object? components = item.GetType().GetProperty("Components")?.GetValue(item);
            if (components is not IEnumerable list)
                return false;
            foreach (var component in list)
            {
                if (component?.GetType().Name == name)
                    return true;
            }
            return false;
        }

        private static bool HasAny(object? enumerable)
        {
            if (enumerable is not IEnumerable list)
                return false;
            foreach (var item in list)
            {
                if (item != null)
                    return true;
            }
            return false;
        }

        private static int GamePixels(string property)
        {
            var value = Find("Barotrauma.GameMain")?.GetProperty(property, BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            return value is int n ? n : 0;
        }

        private static Type? Find(string fullName)
        {
            return Type.GetType(fullName + ", Barotrauma", throwOnError: false) ?? VRManager.FindType(fullName);
        }
    }
}
