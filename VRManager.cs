using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;

namespace BarotraumaVR
{
    internal static class VRModule
    {
        [ModuleInitializer]
        internal static void Init()
        {
            VRManager.Initialize();
        }
    }
}

namespace BarotraumaVR
{
    /// <summary>
    /// Main VR manager - coordinates VR session lifecycle and rendering.
    /// Inspired by CasualtyVR's architecture but adapted for MonoGame/Barotrauma.
    /// </summary>
    public static class VRManager
    {
        private static Harmony? harmonyInstance;
        private static bool isInitialized = false;
        private static bool isVRActive = false;
        private static bool xrBridgeInitialized = false;
        private static bool stereoRendererInitialized = false;
        internal static object? graphicsDevice;  // Internal so VrDualPassStereo can access it
        private static object? backBufferTexture;
        private static object? d3dDevice;
        private static object? d3dContext;
        private static object? monitorCopy;
        private static MethodInfo? copyResource;
        private static bool copyDestFirst = true;
        private static bool monitorCopyFailed;
        private static bool loggedMonitor;
        private static IntPtr d3d11DevicePtr = IntPtr.Zero;
        private static System.IO.StreamWriter? logFile;
        private static readonly object logLock = new object();

        public static bool IsInitialized => isInitialized;
        public static bool IsVRActive => isVRActive;
        public static bool XrBridgeReady => xrBridgeInitialized;
        public static bool StereoReady => stereoRendererInitialized;

        /// <summary>
        /// Initialize VR system - apply Harmony patches and set up OpenXR bridge.
        /// Called by ACsMod.Mod.Load()
        /// </summary>
        public static void Initialize()
        {
            if (harmonyInstance == null)
            {
                try
                {
                    harmonyInstance = new Harmony("com.barotraumavr.mod");
                    ApplyPatches();
                    Log("[BarotraumaVR] Draw patches applied");
                }
                catch (Exception ex)
                {
                    Log($"[BarotraumaVR] Draw patch skipped: {ex.Message}");
                }
            }

            isInitialized = true;
            // Server script starts first and may not see client types. Retry until the hooks stick.
            if (harmonyInstance != null)
            {
                    InputPatches.Apply(harmonyInstance);
                    VrSpriteBatchPatch.Apply(harmonyInstance);
                    VrWallDepth.Apply(harmonyInstance);
                    VrHud.Apply(harmonyInstance);
                    VrSuitVision.Apply(harmonyInstance);
                    VrFirstPerson.Apply(harmonyInstance);
            }
        }

        public static void Log(string message)
        {
            Console.WriteLine(message);
            try
            {
                var debugConsole = FindType("Barotrauma.DebugConsole");
                if (debugConsole == null)
                    return;

                foreach (var method in debugConsole.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (method.Name != "NewMessage")
                        continue;
                    var parameters = method.GetParameters();
                    if (parameters.Length == 3 && parameters[0].ParameterType == typeof(string))
                    {
                        method.Invoke(null, new object?[] { message, null, false });
                        return;
                    }
                }
            }
            catch
            {
                // The debug console is not up yet during early load.
            }
        }

        /// <summary>
        /// Apply Harmony patches to intercept Barotrauma's rendering.
        /// </summary>
        private static void ApplyPatches()
        {
            if (harmonyInstance == null)
            {
                throw new InvalidOperationException("Harmony instance not created");
            }

            // Patch GameScreen.Draw() to intercept rendering
            var gameScreenType = Type.GetType("Barotrauma.GameScreen, Barotrauma");
            if (gameScreenType == null)
            {
                Console.WriteLine("[BarotraumaVR] WARNING: GameScreen type not found");
                return;
            }

            var graphicsDeviceType = FindType("Microsoft.Xna.Framework.Graphics.GraphicsDevice");
            var spriteBatchType = FindType("Microsoft.Xna.Framework.Graphics.SpriteBatch");
            if (graphicsDeviceType == null || spriteBatchType == null)
            {
                Console.WriteLine("[BarotraumaVR] WARNING: MonoGame GraphicsDevice/SpriteBatch not found");
                return;
            }

            var drawMethod = gameScreenType.GetMethod("Draw",
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(double), graphicsDeviceType, spriteBatchType },
                null);

            if (drawMethod == null)
            {
                Console.WriteLine("[BarotraumaVR] WARNING: GameScreen.Draw method not found");
                return;
            }

            var prefixMethod = typeof(GameScreenPatches).GetMethod("Draw_Prefix", BindingFlags.Public | BindingFlags.Static);
            var postfixMethod = typeof(GameScreenPatches).GetMethod("Draw_Postfix", BindingFlags.Public | BindingFlags.Static);

            if (prefixMethod != null)
            {
                harmonyInstance.Patch(drawMethod, prefix: new HarmonyMethod(prefixMethod));
                Console.WriteLine("[BarotraumaVR] GameScreen.Draw prefix patch applied");
            }

            if (postfixMethod != null)
            {
                harmonyInstance.Patch(drawMethod, postfix: new HarmonyMethod(postfixMethod));
                Console.WriteLine("[BarotraumaVR] GameScreen.Draw postfix patch applied");
            }

            GameScreenPatches.GameDraw = drawMethod;

            var gameMainType = Type.GetType("Barotrauma.GameMain, Barotrauma") ?? FindType("Barotrauma.GameMain");
            if (gameMainType != null)
            {
                MethodInfo? gameDraw = null;
                foreach (var method in gameMainType.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
                {
                    if (method.Name == "Draw" && method.GetParameters().Length == 1 && method.DeclaringType == gameMainType)
                    {
                        gameDraw = method;
                        break;
                    }
                }

                if (gameDraw != null)
                {
                    var gmPrefix = typeof(GameScreenPatches).GetMethod(nameof(GameScreenPatches.Game_Draw_Prefix), BindingFlags.Public | BindingFlags.Static);
                    var gmPostfix = typeof(GameScreenPatches).GetMethod(nameof(GameScreenPatches.Game_Draw_Postfix), BindingFlags.Public | BindingFlags.Static);
                    if (gmPrefix != null)
                        harmonyInstance.Patch(gameDraw, prefix: new HarmonyMethod(gmPrefix));
                    if (gmPostfix != null)
                        harmonyInstance.Patch(gameDraw, postfix: new HarmonyMethod(gmPostfix));
                    Console.WriteLine("[BarotraumaVR] GameMain.Draw frame fence applied");
                }
            }

        }

        public static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(fullName, throwOnError: false);
                if (type != null)
                    return type;
            }

            return null;
        }

        /// <summary>
        /// Enable VR rendering (F10 toggle in CasualtyVR style).
        /// </summary>
        public static void EnableVR()
        {
            if (!isInitialized)
            {
                Console.WriteLine("[BarotraumaVR] Cannot enable VR - not initialized");
                return;
            }

            // Initialize XrBridge on first VR enable
            if (!xrBridgeInitialized)
            {
                Log("[BarotraumaVR] First VR enable - initializing OpenXR...");
                if (!InitializeXrBridge())
                {
                    Log("[BarotraumaVR] Failed to initialize XrBridge - VR unavailable");
                    return;
                }
            }

            // Initialize stereo renderer on first VR enable
            if (!stereoRendererInitialized)
            {
                VrStereoRenderer.Initialize();
                VrDualPassRenderer.Initialize();
                stereoRendererInitialized = true;
            }

            // Do not create eye render targets or call bvr_xr_submit_stereo here.
            // Those pointers are not ID3D11Texture2D, and xrWaitSwapchainImage waits forever.

            // Startup runs before MonoGame is visible. GraphicsDevice proves it is loaded now.
            if (harmonyInstance == null)
            {
                Log("[VrSpriteBatchPatch] Harmony instance is null, cannot patch SpriteBatch.Begin");
            }
            else
            {
                VrSpriteBatchPatch.Apply(harmonyInstance, graphicsDevice);
                VrSpriteBatchPatch.LogStatus();
                VrWallDepth.Apply(harmonyInstance);
                VrHud.Apply(harmonyInstance);
                VrSuitVision.Apply(harmonyInstance);
                VrFirstPerson.Apply(harmonyInstance);
            }

            isVRActive = true;
            SetRunInBackground(true);
            Console.WriteLine("[BarotraumaVR] VR enabled - look at your headset!");
        }

        /// <summary>
        /// Initialize OpenXR bridge with MonoGame GraphicsDevice.
        /// </summary>
        private static bool InitializeXrBridge()
        {
            try
            {
                // Get GraphicsDevice from GameMain (Barotrauma singleton)
                var gameMainType = Type.GetType("Barotrauma.GameMain, Barotrauma");
                if (gameMainType == null)
                {
                    Log("[BarotraumaVR] ERROR: GameMain type not found");
                    return false;
                }

                var instanceProp = gameMainType.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
                if (instanceProp == null)
                {
                    Log("[BarotraumaVR] ERROR: GameMain.Instance not found");
                    return false;
                }

                var gameMainInstance = instanceProp.GetValue(null);
                if (gameMainInstance == null)
                {
                    Log("[BarotraumaVR] ERROR: GameMain.Instance is null - start a game first!");
                    return false;
                }

                // GraphicsDevice is inherited from Microsoft.Xna.Framework.Game.
                // GraphicsDeviceManager is a static property, not an instance field.
                var gdProp = gameMainType.GetProperty("GraphicsDevice", BindingFlags.Public | BindingFlags.Instance);
                graphicsDevice = gdProp?.GetValue(gameMainInstance);
                if (graphicsDevice == null)
                {
                    Log("[BarotraumaVR] ERROR: GameMain.GraphicsDevice is null");
                    return false;
                }

                Log("[BarotraumaVR] GraphicsDevice obtained successfully");

                d3d11DevicePtr = GetD3D11DevicePointer(graphicsDevice);
                if (d3d11DevicePtr == IntPtr.Zero)
                {
                    Log("[BarotraumaVR] ERROR: Could not get D3D11 device pointer");
                    return false;
                }

                Log($"[BarotraumaVR] D3D11 Device: 0x{d3d11DevicePtr:X}");

                var backBuffer = GetBackBufferPointer(graphicsDevice);
                if (backBuffer == IntPtr.Zero)
                {
                    Log("[BarotraumaVR] ERROR: Could not get the D3D11 backbuffer texture");
                    return false;
                }

                Log($"[BarotraumaVR] D3D11 BackBuffer: 0x{backBuffer:X}");

                PreferSteamVrRuntime();

                // bvr_xr_init takes an ID3D11Texture2D* and queries the device from it.
                string error;
                bool success = XrBridge.Initialize(backBuffer, out error);

                if (success)
                {
                    Log("[BarotraumaVR] OpenXR initialized successfully!");
                    DrainNativeLog();

                    XrBridge.Info info;
                    XrBridge.bvr_xr_get_info(out info);
                    Log($"[BarotraumaVR] System: {info.SystemName}, eye {info.RecommendedWidth}x{info.RecommendedHeight}");

                    xrBridgeInitialized = true;
                    return true;
                }
                else
                {
                    Log($"[BarotraumaVR] OpenXR init failed: {error}");
                    return false;
                }
            }
            catch (Exception ex)
            {
                Log($"[BarotraumaVR] Exception during XrBridge init: {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// If SteamVR is already running, bind OpenXR to it so Meta/Oculus
        /// does not steal the headset and close SteamVR.
        /// Leaves XR_RUNTIME_JSON alone when the user already set one.
        /// </summary>
        private static void PreferSteamVrRuntime()
        {
            string? existing = Environment.GetEnvironmentVariable("XR_RUNTIME_JSON");
            if (!string.IsNullOrWhiteSpace(existing))
            {
                Log($"[BarotraumaVR] XR_RUNTIME_JSON already set: {existing}");
                return;
            }

            bool steamVrRunning = SteamVrProcessRunning();
            string? json = FindSteamVrRuntimeJson();
            if (string.IsNullOrEmpty(json))
            {
                if (steamVrRunning)
                    Log("[BarotraumaVR] SteamVR is running, but steamxr_win64.json was not found");
                return;
            }

            if (!steamVrRunning)
            {
                Log($"[BarotraumaVR] SteamVR OpenXR found at {json}, not forcing it (SteamVR is not running)");
                return;
            }

            Environment.SetEnvironmentVariable("XR_RUNTIME_JSON", json);
            Log($"[BarotraumaVR] Using SteamVR OpenXR so SteamVR stays: {json}");
        }

        private static bool SteamVrProcessRunning()
        {
            try
            {
                return Process.GetProcessesByName("vrserver").Length > 0
                    || Process.GetProcessesByName("vrmonitor").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        private static string? FindSteamVrRuntimeJson()
        {
            try
            {
                foreach (var process in Process.GetProcessesByName("vrserver"))
                {
                    string? exe;
                    try { exe = process.MainModule?.FileName; }
                    catch { continue; }
                    if (string.IsNullOrEmpty(exe))
                        continue;

                    // ...\SteamVR\bin\win64\vrserver.exe
                    string? steamVr = Path.GetDirectoryName(Path.GetDirectoryName(Path.GetDirectoryName(exe)));
                    if (string.IsNullOrEmpty(steamVr))
                        continue;
                    string json = Path.Combine(steamVr, "steamxr_win64.json");
                    if (File.Exists(json))
                        return json;
                }
            }
            catch { }

            foreach (string candidate in new[]
            {
                Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, @"..\SteamVR\steamxr_win64.json")),
                Path.Combine(@"C:\Program Files (x86)\Steam\steamapps\common\SteamVR", "steamxr_win64.json"),
                @"D:\SteamLibrary\steamapps\common\SteamVR\steamxr_win64.json",
            })
            {
                try
                {
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// Get D3D11 device pointer from MonoGame GraphicsDevice.
        /// This is platform-specific and may need adjustment.
        /// </summary>
        private static IntPtr GetD3D11DevicePointer(object graphicsDevice)
        {
            try
            {
                // MonoGame Windows stores a SharpDX.Direct3D11.Device in GraphicsDevice.Handle.
                var handleProp = graphicsDevice.GetType().GetProperty("Handle", BindingFlags.Public | BindingFlags.Instance);
                var handle = handleProp?.GetValue(graphicsDevice);
                var native = handle?.GetType().GetProperty("NativePointer")?.GetValue(handle);
                if (native is IntPtr ptr && ptr != IntPtr.Zero)
                {
                    Log("[BarotraumaVR] Got D3D11 device via GraphicsDevice.Handle.NativePointer");
                    return ptr;
                }

                Log("[BarotraumaVR] WARNING: GraphicsDevice.Handle has no NativePointer");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                Log($"[BarotraumaVR] Exception getting D3D11 pointer: {ex.GetType().Name}: {ex.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// SharpDX swapchain backbuffer. The native bridge calls GetDevice on this texture.
        /// The managed wrapper is kept alive so the COM pointer stays valid.
        /// </summary>
        private static IntPtr GetBackBufferPointer(object graphicsDevice)
        {
            try
            {
                var swapField = graphicsDevice.GetType().GetField("_swapChain", BindingFlags.NonPublic | BindingFlags.Instance);
                var swap = swapField?.GetValue(graphicsDevice);
                var deviceField = graphicsDevice.GetType().GetField("_d3dDevice", BindingFlags.NonPublic | BindingFlags.Instance);
                var device = deviceField?.GetValue(graphicsDevice);
                if (swap == null || device == null)
                {
                    Log("[BarotraumaVR] ERROR: _swapChain or _d3dDevice is missing");
                    return IntPtr.Zero;
                }

                var textureType = device.GetType().Assembly.GetType("SharpDX.Direct3D11.Texture2D");
                if (textureType == null)
                {
                    Log("[BarotraumaVR] ERROR: SharpDX.Direct3D11.Texture2D was not found");
                    return IntPtr.Zero;
                }

                var getBackBuffer = swap.GetType().GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "GetBackBuffer" && m.IsGenericMethodDefinition);
                if (getBackBuffer == null)
                {
                    Log("[BarotraumaVR] ERROR: SwapChain.GetBackBuffer was not found");
                    return IntPtr.Zero;
                }

                backBufferTexture = getBackBuffer.MakeGenericMethod(textureType).Invoke(swap, new object[] { 0 });
                var native = backBufferTexture?.GetType().GetProperty("NativePointer")?.GetValue(backBufferTexture);
                if (native is IntPtr ptr && ptr != IntPtr.Zero)
                    return ptr;

                Log("[BarotraumaVR] ERROR: backbuffer NativePointer is zero");
                return IntPtr.Zero;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log($"[BarotraumaVR] Exception getting backbuffer: {inner.GetType().Name}: {inner.Message}");
                return IntPtr.Zero;
            }
        }

        /// <summary>
        /// HUD capture switches the render target and D3D11 discards the backbuffer.
        /// Keep a copy so the PC window can show the same picture as the headset.
        /// </summary>
        internal static void SaveMonitorPicture()
        {
            try
            {
                if (!EnsureMonitorCopy() || monitorCopy == null || backBufferTexture == null || copyResource == null || d3dContext == null)
                    return;
                CopyGpu(monitorCopy, backBufferTexture);
            }
            catch (Exception ex)
            {
                monitorCopyFailed = true;
                Log($"[BarotraumaVR] Monitor copy save failed: {ex.InnerException?.Message ?? ex.Message}");
            }
        }

        internal static void RestoreMonitorPicture()
        {
            try
            {
                if (monitorCopy == null || backBufferTexture == null || copyResource == null || d3dContext == null)
                    return;
                CopyGpu(backBufferTexture, monitorCopy);
                if (!loggedMonitor)
                {
                    loggedMonitor = true;
                    Log("[BarotraumaVR] Monitor shows the headset picture");
                }
            }
            catch (Exception ex)
            {
                if (!loggedMonitor)
                {
                    loggedMonitor = true;
                    Log($"[BarotraumaVR] Monitor copy restore failed: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
        }

        private static bool EnsureMonitorCopy()
        {
            if (monitorCopy != null)
                return true;
            if (monitorCopyFailed || graphicsDevice == null || backBufferTexture == null)
                return false;

            d3dDevice = graphicsDevice.GetType().GetField("_d3dDevice", BindingFlags.NonPublic | BindingFlags.Instance)?.GetValue(graphicsDevice);
            d3dContext = d3dDevice?.GetType().GetProperty("ImmediateContext")?.GetValue(d3dDevice);
            if (d3dDevice == null || d3dContext == null)
            {
                monitorCopyFailed = true;
                Log("[BarotraumaVR] D3D11 context for the monitor copy was not found");
                return false;
            }

            copyResource = d3dContext.GetType().GetMethod("CopyResource", BindingFlags.Public | BindingFlags.Instance);
            if (copyResource == null)
            {
                monitorCopyFailed = true;
                Log("[BarotraumaVR] CopyResource was not found");
                return false;
            }

            string first = copyResource.GetParameters()[0].Name ?? "";
            copyDestFirst = first.StartsWith("dest", StringComparison.OrdinalIgnoreCase)
                || first.Contains("Dst", StringComparison.Ordinal)
                || first.StartsWith("pDst", StringComparison.OrdinalIgnoreCase);

            object? desc = backBufferTexture.GetType().GetProperty("Description")?.GetValue(backBufferTexture);
            if (desc == null)
            {
                monitorCopyFailed = true;
                return false;
            }

            var descType = desc.GetType();
            void Zero(string name)
            {
                var prop = descType.GetProperty(name);
                if (prop != null)
                {
                    object value = prop.PropertyType.IsEnum ? Enum.ToObject(prop.PropertyType, 0) : 0;
                    prop.SetValue(desc, value);
                    return;
                }
                descType.GetField(name)?.SetValue(desc, 0);
            }
            Zero("OptionFlags");
            Zero("CpuAccessFlags");

            var ctor = backBufferTexture.GetType().GetConstructor(new[] { d3dDevice.GetType(), descType });
            monitorCopy = ctor?.Invoke(new[] { d3dDevice, desc });
            if (monitorCopy == null)
            {
                monitorCopyFailed = true;
                Log("[BarotraumaVR] Monitor copy texture was not created");
                return false;
            }

            Log("[BarotraumaVR] Monitor copy texture ready");
            return true;
        }

        private static void CopyGpu(object destination, object source)
        {
            if (copyDestFirst)
                copyResource!.Invoke(d3dContext, new[] { destination, source });
            else
                copyResource!.Invoke(d3dContext, new[] { source, destination });
        }

        private static void SetRunInBackground(bool on)
        {
            try
            {
                var game = FindType("Barotrauma.GameMain")?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                game?.GetType().GetProperty("InactiveSleepTime")?.SetValue(game, on ? TimeSpan.Zero : TimeSpan.FromMilliseconds(20));
            }
            catch
            {
            }
        }

        /// <summary>
        /// Disable VR rendering, return to flat mode.
        /// </summary>
        public static void DisableVR()
        {
            if (!isVRActive)
            {
                return;
            }

            isVRActive = false;
            SetRunInBackground(false);
            VrHud.SkipWorld = false;
            VrFirstPerson.OnVrDisabled();
            GameScreenPatches.CancelPendingFrame();
            GameScreenPatches.ResetControllerLog();
            Log("[BarotraumaVR] VR disabled - back to flat screen");
        }

        /// <summary>
        /// Toggle VR on/off.
        /// </summary>
        public static void ToggleVR()
        {
            if (isVRActive)
            {
                DisableVR();
            }
            else
            {
                EnableVR();
            }
        }

        public static void DrainNativeLog()
        {
            foreach (var line in XrBridge.GetLog().Split('\n'))
            {
                var text = line.Trim();
                if (text.Length > 0)
                    Log("[BarotraumaVR] " + text);
            }
        }
    }

    /// <summary>
    /// Harmony patches for GameScreen.Draw().
    /// Intercepts rendering to inject VR stereo rendering.
    /// </summary>
    public static class GameScreenPatches
    {
        private static int frameCount = 0;
        private static bool hasLoggedFirstFrame = false;
        private static bool loggedController;
        private static int framesWithoutController;
        private static bool inSecondEye;
        private static bool cameraShifted;
        private static bool hudNeutral;
        private static bool xrPending;
        private static bool xrSubmitted;
        private static bool loggedHudOverlayFailure;
        private static object? activeScreen;
        private static object? savedPosition;
        private static object? savedPrevPosition;
        private const float EyeParallax = 0f;

        public static MethodInfo? GameDraw { get; set; }

        public static void CancelPendingFrame()
        {
            if (!xrPending)
                return;
            XrBridge.bvr_xr_end_frame();
            xrPending = false;
            xrSubmitted = false;
        }

        /// <summary>
        /// OpenXR wait/end must wrap every Game.Draw, not only GameScreen.
        /// Leaving a round without xrEndFrame is what froze the headset on the menu.
        /// </summary>
        public static void Game_Draw_Prefix()
        {
            if (!VRManager.IsVRActive || !VRManager.XrBridgeReady || inSecondEye)
                return;

            XrBridge.bvr_xr_poll();
            if (xrPending)
            {
                XrBridge.bvr_xr_end_frame();
                xrPending = false;
            }

            XrBridge.FrameInfo frameInfo;
            if (XrBridge.bvr_xr_wait_frame(out frameInfo) == 0)
            {
                VRManager.Log("[BarotraumaVR] xrWaitFrame failed - VR session lost?");
                VRManager.DisableVR();
                return;
            }

            xrPending = true;
            xrSubmitted = false;
            if (VRManager.StereoReady)
                VrStereoRenderer.UpdateFrame(frameInfo);
            VrPointer.Update(frameInfo);
            NoteControllers();
            if (frameInfo.FrameIndex <= 5)
                VRManager.DrainNativeLog();
        }

        public static void Game_Draw_Postfix()
        {
            if (!xrPending || inSecondEye)
                return;

            try
            {
                if (!xrSubmitted)
                {
                    if (XrBridge.bvr_xr_begin_frame() != 0)
                    {
                        CopyEye(0);
                        CopyEye(1);
                        XrBridge.bvr_xr_release_swapchain(0);
                        XrBridge.bvr_xr_release_swapchain(1);
                    }
                }
            }
            catch (Exception ex)
            {
                VRManager.Log($"[BarotraumaVR] Menu frame submit failed: {ex.Message}");
            }
            finally
            {
                XrBridge.bvr_xr_end_frame();
                xrPending = false;
                xrSubmitted = false;
                VrHud.SkipWorld = false;
                VrSpriteBatchPatch.SetCurrentEye(-1);
            }
        }

        /// <summary>
        /// Called before GameScreen.Draw() - prepare for VR rendering if active.
        /// F10 is handled by InputPatches on PlayerInput.Update.
        /// </summary>
        public static void Draw_Prefix(object __instance)
        {
            if (!VRManager.IsVRActive || !VRManager.XrBridgeReady || inSecondEye)
            {
                return;
            }

            if (!xrPending)
            {
                XrBridge.bvr_xr_poll();
                XrBridge.FrameInfo fallback;
                if (XrBridge.bvr_xr_wait_frame(out fallback) == 0)
                {
                    VRManager.Log("[BarotraumaVR] xrWaitFrame failed - VR session lost?");
                    VRManager.DisableVR();
                    return;
                }
                xrPending = true;
                xrSubmitted = false;
                if (VRManager.StereoReady)
                    VrStereoRenderer.UpdateFrame(fallback);
                VrPointer.Update(fallback);
            }

            VrWallDepth.Retry();
            VrSuitVision.Retry();
            VrFirstPerson.Retry();
            VrFirstPerson.PrepareFrame();
            VrFirstPerson.HideGameHud();
            VrLevelProbe.SampleOncePerLevel();

            activeScreen = __instance;
            hudNeutral = false;
            VrHud.SkipWorld = true;
            VrHud.Retry();
            VrDualPassRenderer.BeginDualPass(__instance);
            cameraShifted = ShiftCamera(__instance, -EyeParallax);

            if (!hasLoggedFirstFrame)
            {
                VRManager.Log("[BarotraumaVR] First VR frame prepared: left eye then right eye");
                hasLoggedFirstFrame = true;
            }
        }

        public static void ResetControllerLog()
        {
            loggedController = false;
            framesWithoutController = 0;
            VrPointer.Reset();
            VrStick.ResetLog();
        }

        private static void NoteControllers()
        {
            if (loggedController)
                return;

            XrBridge.bvr_xr_get_input(out var input);
            var hands = input.Hands;
            if (hands != null)
            {
                for (int i = 0; i < hands.Length && i < 2; i++)
                {
                    if (hands[i].PoseValid == 0)
                        continue;
                    var p = hands[i].AimPosition;
                    VRManager.Log($"[BarotraumaVR] {(i == 0 ? "Left" : "Right")} controller ({p.X:0.00}, {p.Y:0.00}, {p.Z:0.00}) trigger {hands[i].Trigger:0.00}");
                    loggedController = true;
                }
            }

            if (!loggedController && ++framesWithoutController == 300)
                VRManager.Log("[BarotraumaVR] Controllers not tracked yet. Wake the controllers and look at the play space.");
        }

        /// <summary>
        /// Called after GameScreen.Draw() - copy this eye, draw the other, then end the VR frame.
        /// </summary>
        public static void Draw_Postfix(object __instance, double deltaTime, object graphics, object spriteBatch)
        {
            if (!VRManager.IsVRActive || !VRManager.XrBridgeReady || inSecondEye)
            {
                return;
            }

            VrDualPassRenderer.EndDualPass(__instance);

            if (XrBridge.bvr_xr_begin_frame() == 0)
            {
                VrHud.SkipWorld = false;
                RestoreCamera(__instance);
                VrFirstPerson.RestoreGameHud();
                VRManager.Log("[BarotraumaVR] xrBeginFrame failed");
                return;
            }

            try
            {
                VrSuitVision.PrepareCopy(graphics);
                CopyEye(0);
                if (cameraShifted && GameDraw != null)
                {
                    ShiftCamera(__instance, EyeParallax);
                    VrSpriteBatchPatch.SetCurrentEye(1);
                    hudNeutral = false;
                    VrHud.SkipWorld = true;
                    inSecondEye = true;
                    VrSuitVision.PrepareCopy(graphics);
                    GameDraw.Invoke(__instance, new[] { deltaTime, graphics, spriteBatch });
                    VrSuitVision.PrepareCopy(graphics);
                    CopyEye(1);
                }
                else
                {
                    VrSuitVision.PrepareCopy(graphics);
                    CopyEye(1);
                }

                VrHud.SkipWorld = false;
                VrSpriteBatchPatch.SetCurrentEye(-1);
                NeutralizeHud();
                VrFirstPerson.RestoreGameHud();
                VRManager.SaveMonitorPicture();
                bool showHud = VrFirstPerson.HudVisible;
                bool showCursor = VrFirstPerson.Active && !showHud;
                if (showHud || showCursor)
                {
                    // The HUD overlay must never take the frame down with it:
                    // an exception here used to skip release_swapchain, the
                    // images stayed acquired forever and the headset froze on
                    // a stale picture. It has its own catch and the swapchains
                    // are released below regardless.
                    try
                    {
                        VrHud.Capture(__instance, graphics, spriteBatch, cursorOnly: showCursor);
                        XrBridge.bvr_xr_overlay_hud(0);
                        XrBridge.bvr_xr_overlay_hud(1);
                    }
                    catch (Exception hudEx)
                    {
                        if (!loggedHudOverlayFailure)
                        {
                            loggedHudOverlayFailure = true;
                            var inner = hudEx.InnerException ?? hudEx;
                            VRManager.Log($"[BarotraumaVR] HUD overlay failed (world picture unaffected): {inner.GetType().Name}: {inner.Message}");
                        }
                    }
                }
                XrBridge.bvr_xr_release_swapchain(0);
                XrBridge.bvr_xr_release_swapchain(1);
                VRManager.RestoreMonitorPicture();
                VrHud.DrawHudOnMonitor(__instance, graphics, spriteBatch);
                xrSubmitted = true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                VRManager.Log($"[BarotraumaVR] Second eye draw failed: {inner.GetType().Name}: {inner.Message}");
            }
            finally
            {
                inSecondEye = false;
                VrHud.SkipWorld = false;
                VrSpriteBatchPatch.SetCurrentEye(-1);
                RestoreCamera(__instance);
                VrFirstPerson.RestoreGameHud();
            }

            frameCount++;
        }

        private static void CopyEye(int eye)
        {
            IntPtr texture = XrBridge.bvr_xr_acquire_swapchain(eye);
            if (texture == IntPtr.Zero)
            {
                if (frameCount < 3)
                    VRManager.Log($"[BarotraumaVR] acquire swapchain eye {eye} failed");
                return;
            }

            if (XrBridge.bvr_xr_copy_source_to_acquired(eye) == 0 && frameCount < 3)
                VRManager.Log($"[BarotraumaVR] copy into swapchain eye {eye} failed");
        }

        private static bool ShiftCamera(object gameScreen, float x)
        {
            try
            {
                var cam = gameScreen.GetType().GetProperty("Cam")?.GetValue(gameScreen);
                if (cam == null)
                    return false;

                var camType = cam.GetType();
                var posProp = camType.GetProperty("Position");
                var prevField = camType.GetField("prevPosition", BindingFlags.NonPublic | BindingFlags.Instance);
                if (posProp == null)
                    return false;

                if (savedPosition == null)
                {
                    savedPosition = posProp.GetValue(cam);
                    savedPrevPosition = prevField?.GetValue(cam);
                }

                var current = posProp.GetValue(cam);
                var vecType = current?.GetType();
                var xField = vecType?.GetField("X");
                var yField = vecType?.GetField("Y");
                if (current == null || xField == null || yField == null || savedPosition == null)
                    return false;

                float y = (float)yField.GetValue(savedPosition)!;
                float originX = (float)xField.GetValue(savedPosition)!;
                var shifted = Activator.CreateInstance(vecType!)!;
                xField.SetValue(shifted, originX + x);
                yField.SetValue(shifted, y);
                posProp.SetValue(cam, shifted);
                prevField?.SetValue(cam, shifted);
                return true;
            }
            catch (Exception ex)
            {
                VRManager.Log($"[BarotraumaVR] Camera shift failed: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// Inventory and the HUD sample the camera while they draw. Put the camera
        /// back before that, so both eyes share one flat interface.
        /// </summary>
        public static void NeutralizeHud()
        {
            if (hudNeutral || savedPosition == null || activeScreen == null)
                return;
            hudNeutral = true;
            try
            {
                var cam = activeScreen.GetType().GetProperty("Cam")?.GetValue(activeScreen);
                var posProp = cam?.GetType().GetProperty("Position");
                var prevField = cam?.GetType().GetField("prevPosition", BindingFlags.NonPublic | BindingFlags.Instance);
                posProp?.SetValue(cam, savedPosition);
                if (savedPrevPosition != null)
                    prevField?.SetValue(cam, savedPrevPosition);
                cam?.GetType().GetMethod("UpdateTransform")?.Invoke(cam, new object[] { false, false });
                if (!loggedHud)
                {
                    loggedHud = true;
                    VRManager.Log("[BarotraumaVR] Inventory is the same in both eyes");
                }
            }
            catch (Exception ex)
            {
                VRManager.Log($"[BarotraumaVR] HUD camera restore failed: {ex.Message}");
            }
        }

        private static bool loggedHud;

        private static void RestoreCamera(object gameScreen)
        {
            if (savedPosition == null)
                return;

            try
            {
                var cam = gameScreen.GetType().GetProperty("Cam")?.GetValue(gameScreen);
                var posProp = cam?.GetType().GetProperty("Position");
                var prevField = cam?.GetType().GetField("prevPosition", BindingFlags.NonPublic | BindingFlags.Instance);
                posProp?.SetValue(cam, savedPosition);
                if (savedPrevPosition != null)
                    prevField?.SetValue(cam, savedPrevPosition);
                cam?.GetType().GetMethod("UpdateTransform")?.Invoke(cam, new object[] { false, true });
            }
            catch (Exception ex)
            {
                VRManager.Log($"[BarotraumaVR] Camera restore failed: {ex.Message}");
            }
            finally
            {
                savedPosition = null;
                savedPrevPosition = null;
                cameraShifted = false;
            }
        }
    }
}
