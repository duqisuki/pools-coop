using System;
using HarmonyLib;

namespace PoolsCoop
{
    // Test-only protection. The save folder (and Steam Auto-Cloud, and the Steam stats/achievements) belong to the owner's real
    // account and are shared by every copy of the game. When [Test] SaveGuard is on, every game method that writes progress is
    // replaced by a no-op that only counts the attempt. Each patch class is skipped entirely (Prepare) when the guard is off.
    // Plugin.GuardSelfTest() proves the patches really run before any test loads a chapter.

    internal static class GuardCounter
    {
        public static int Blocked;
        public static string Last = "";

        public static bool Block(string what)
        {
            Blocked++;
            Last = what;
            return false; // skip the original
        }
    }

    [HarmonyPatch(typeof(Backrooms.Features.AutoSave.AutoSave), nameof(Backrooms.Features.AutoSave.AutoSave.TrySave))]
    internal static class GuardAutoSaveTrySave
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("AutoSave.TrySave");
    }

    [HarmonyPatch(typeof(Backrooms.Features.AutoSave.AutoSave), nameof(Backrooms.Features.AutoSave.AutoSave.ResetSave))]
    internal static class GuardAutoSaveReset
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("AutoSave.ResetSave");
    }

    [HarmonyPatch(typeof(Backrooms.Features.AutoSave.Saver), nameof(Backrooms.Features.AutoSave.Saver.Save),
        new[] { typeof(string), typeof(Backrooms.Features.AutoSave.LevelState) })]
    internal static class GuardSaverSave
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix(ref bool __result) { __result = false; return GuardCounter.Block("Saver.Save(LevelState)"); }
    }

    [HarmonyPatch(typeof(Backrooms.Features.Levels.LevelManager), nameof(Backrooms.Features.Levels.LevelManager.Finish))]
    internal static class GuardLevelFinish
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("LevelManager.Finish");
    }

    [HarmonyPatch(typeof(Backrooms.Features.Levels.LevelManager), nameof(Backrooms.Features.Levels.LevelManager.SetNewUnlockState))]
    internal static class GuardLevelUnlock
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("LevelManager.SetNewUnlockState");
    }

    [HarmonyPatch(typeof(Backrooms.Features.Levels.LevelManager), nameof(Backrooms.Features.Levels.LevelManager.DeleteSaveFile))]
    internal static class GuardLevelDelete
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("LevelManager.DeleteSaveFile");
    }

    [HarmonyPatch(typeof(Backrooms.Features.Levels.LevelManager), nameof(Backrooms.Features.Levels.LevelManager.ResetProgress))]
    internal static class GuardLevelReset
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("LevelManager.ResetProgress");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.ExternalManagers), nameof(Backrooms.Features.External.ExternalManagers.TryUpdateStat))]
    internal static class GuardExternalStat
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("ExternalManagers.TryUpdateStat");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.ExternalManagers), nameof(Backrooms.Features.External.ExternalManagers.TryUnlockAchievement),
        new[] { typeof(string) })]
    internal static class GuardExternalUnlockId
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("ExternalManagers.TryUnlockAchievement(id)");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.ExternalManagers), nameof(Backrooms.Features.External.ExternalManagers.TryUnlockAchievement),
        new[] { typeof(Backrooms.Features.AchievementAsset) })]
    internal static class GuardExternalUnlockAsset
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("ExternalManagers.TryUnlockAchievement(asset)");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.ExternalManagers), nameof(Backrooms.Features.External.ExternalManagers.ResetStats))]
    internal static class GuardExternalResetStats
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix(ref bool __result) { __result = false; return GuardCounter.Block("ExternalManagers.ResetStats"); }
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.Steam.SteamManager), nameof(Backrooms.Features.External.Steam.SteamManager.TryUpdateStat))]
    internal static class GuardSteamStat
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("SteamManager.TryUpdateStat");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.Steam.SteamManager), nameof(Backrooms.Features.External.Steam.SteamManager.TryUnlockAchievement),
        new[] { typeof(string) })]
    internal static class GuardSteamUnlock
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix() => GuardCounter.Block("SteamManager.TryUnlockAchievement");
    }

    [HarmonyPatch(typeof(Backrooms.Features.External.Steam.SteamManager), nameof(Backrooms.Features.External.Steam.SteamManager.ResetStats))]
    internal static class GuardSteamResetStats
    {
        private static bool Prepare() => Plugin.SaveGuard;
        private static bool Prefix(ref bool __result) { __result = false; return GuardCounter.Block("SteamManager.ResetStats"); }
    }
}
