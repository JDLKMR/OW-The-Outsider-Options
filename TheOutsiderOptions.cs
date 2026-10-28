using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using OWML.Common;
using OWML.ModHelper;
using UnityEngine;

namespace TheOutsiderOptions
{
    /// <summary>
    /// Companion options for The Outsider (SBtT.TheOutsider). Both options are live: toggling them in the
    /// OWML mod options menu takes effect immediately (OWML calls Configure() on every change).
    ///
    ///   respectFreezeTime - undo The Outsider's two overrides of the vanilla "freeze time while reading" options.
    ///   vanillaEnding     - skip The Outsider's custom Eye of the Universe cutscene/credits so the normal ending plays
    ///                       (applies the next time the Eye scene loads).
    ///
    /// Nothing here references TheOutsider.dll; The Outsider's code is found by name at runtime, so with the
    /// Outsider absent (or renamed in an update) the affected option simply does nothing.
    /// </summary>
    public class OutsiderOptionsMod : ModBehaviour
    {
        const string OutsiderPatchClass = "TheOutsider.OuterWildsHandling.OWPatches";
        const string OutsiderEndingClass = "TheOutsider.NewEndingHandler";

        public static bool RespectFreezeTime { get; private set; } = true;
        public static bool VanillaEnding { get; private set; }

        static OutsiderOptionsMod _instance;

        Harmony _harmony;
        bool _ready;
        bool _endingPatched;

        /// <summary>An Outsider prefix that can be removed (option on) and put back (option off).</summary>
        class FreezeTarget
        {
            public MethodBase Original;
            public string OutsiderPatchName;
            public MethodInfo Removed;   // set while we have the Outsider's prefix unpatched
        }
        List<FreezeTarget> _freezeTargets;

        // OWML calls this from Init() (before Start) and again whenever a setting changes in the menu.
        public override void Configure(IModConfig config)
        {
            RespectFreezeTime = config.GetSettingsValue<bool>("respectFreezeTime");
            VanillaEnding = config.GetSettingsValue<bool>("vanillaEnding");

            if (_ready) ApplyFreezeTimeOption();
        }

        void Start()
        {
            _instance = this;
            _harmony = new Harmony("YourName.TheOutsiderOptions");

            _freezeTargets = new List<FreezeTarget>
            {
                new FreezeTarget
                {
                    Original = AccessTools.Method(typeof(NomaiTranslatorProp), nameof(NomaiTranslatorProp.UpdateTimeFreeze)),
                    OutsiderPatchName = "UpdateTimeFreeze"
                },
                new FreezeTarget
                {
                    Original = AccessTools.Method(typeof(ShipLogController), nameof(ShipLogController.EnterShipComputer)),
                    OutsiderPatchName = "EnterShipComputer"
                },
            };

            // Our own attribute patches (ShipLogFallback below).
            _harmony.PatchAll(Assembly.GetExecutingAssembly());

            // The Outsider applies its patches in its own Start(). Unity runs every mod's Start() before the first
            // Update(), so the next update is always after the Outsider has patched.
            ModHelper.Events.Unity.FireOnNextUpdate(() =>
            {
                _ready = true;
                TryPatchEnding();
                ApplyFreezeTimeOption();
            });

            // Safety net; both calls are idempotent.
            LoadManager.OnCompleteSceneLoad += (_, __) =>
            {
                TryPatchEnding();
                ApplyFreezeTimeOption();
            };
        }

        // ---------------------------------------------------------------------------------------------------
        // Freeze time
        // ---------------------------------------------------------------------------------------------------

        void ApplyFreezeTimeOption()
        {
            foreach (var target in _freezeTargets)
            {
                if (target.Original == null) continue;

                if (RespectFreezeTime) RemoveOutsiderPrefix(target);
                else RestoreOutsiderPrefix(target);
            }
        }

        /// <summary>Finds the Outsider's prefix by identity (class + method name), whichever Harmony instance owns it.</summary>
        void RemoveOutsiderPrefix(FreezeTarget target)
        {
            var info = Harmony.GetPatchInfo(target.Original);
            var theirs = info?.Prefixes.FirstOrDefault(p =>
                p.PatchMethod.DeclaringType?.FullName == OutsiderPatchClass &&
                p.PatchMethod.Name == target.OutsiderPatchName);
            if (theirs == null) return;

            target.Removed = theirs.PatchMethod;
            _harmony.Unpatch(target.Original, target.Removed);
            Log($"Removed The Outsider's {target.OutsiderPatchName} override.");
        }

        void RestoreOutsiderPrefix(FreezeTarget target)
        {
            if (target.Removed == null) return;

            _harmony.Patch(target.Original, prefix: new HarmonyMethod(target.Removed));
            Log($"Restored The Outsider's {target.OutsiderPatchName} override.");
            target.Removed = null;
        }

        // ---------------------------------------------------------------------------------------------------
        // Ending
        // ---------------------------------------------------------------------------------------------------

        /// <summary>
        /// The Outsider's ending lives in NewEndingHandler. Two things matter:
        ///
        /// Initialize(AssetBundle), run when the Eye scene loads, does more than set up the cutscene: it instantiates an
        /// unnamed sector with a 3000 m proximity trigger and makes it the PARENT of the vanilla Sector_EyeOfTheUniverse.
        /// Detectors (player, NPCs, items) are only tracked through root sectors, so the vanilla Eye sector then only
        /// sees what that sphere sees, which breaks sector-based culling/LOD and item placement in the vanilla ending.
        /// With the option on we skip Initialize entirely, so the Eye scene is left exactly as vanilla.
        ///
        /// PlayAnimation() is what the Outsider's trigger calls to start the cutscene. Skipping it too covers the case
        /// where the option is switched on after the Eye scene has already been initialized (that visit would still
        /// have the sector rewiring; the next Eye load won't).
        /// </summary>
        void TryPatchEnding()
        {
            if (_endingPatched) return;

            var type = AccessTools.TypeByName(OutsiderEndingClass);
            if (type == null) return;   // Outsider not loaded (yet)

            var initialize = AccessTools.Method(type, "Initialize", new[] { typeof(AssetBundle) });
            var playAnimation = AccessTools.Method(type, "PlayAnimation", Type.EmptyTypes);
            if (initialize == null && playAnimation == null) return;

            if (initialize != null)
                _harmony.Patch(initialize, prefix: new HarmonyMethod(typeof(OutsiderOptionsMod), nameof(InitializePrefix)));
            if (playAnimation != null)
                _harmony.Patch(playAnimation, prefix: new HarmonyMethod(typeof(OutsiderOptionsMod), nameof(PlayAnimationPrefix)));

            _endingPatched = true;
        }

        // Returning false skips the original method.
        internal static bool InitializePrefix()
        {
            if (!VanillaEnding) return true;

            Log("Vanilla Eye Ending is on: leaving the Eye scene untouched (skipping The Outsider's ending setup).");
            return false;
        }

        internal static bool PlayAnimationPrefix()
        {
            if (!VanillaEnding) return true;

            Log("Vanilla Eye Ending is on: skipping The Outsider's ending cutscene.");
            return false;
        }

        // ---------------------------------------------------------------------------------------------------

        static void Log(string message) =>
            _instance?.ModHelper.Console.WriteLine(message, MessageType.Info);
    }

    /// <summary>
    /// Belt and braces for the ship log: even if the Outsider's prefix can't be removed (e.g. renamed in an update),
    /// put the player's own setting back right after it has been overwritten.
    /// First prefix remembers the real value, last prefix restores it, so the original method sees the player's choice.
    /// Always read PlayerData._settingsSave fresh; SetSettingsData() replaces the object.
    /// </summary>
    [HarmonyPatch(typeof(ShipLogController), nameof(ShipLogController.EnterShipComputer))]
    public static class ShipLogFallback
    {
        static bool _playerChoice;

        [HarmonyPrefix, HarmonyPriority(Priority.First)]
        static void Capture()
        {
            if (!OutsiderOptionsMod.RespectFreezeTime) return;
            _playerChoice = PlayerData._settingsSave.freezeTimeWhileReadingShipLog;
        }

        [HarmonyPrefix, HarmonyPriority(Priority.Last)]
        static void Restore()
        {
            if (!OutsiderOptionsMod.RespectFreezeTime) return;
            PlayerData._settingsSave.freezeTimeWhileReadingShipLog = _playerChoice;
        }
    }
}
