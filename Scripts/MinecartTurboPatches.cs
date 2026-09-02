using HarmonyLib;

namespace MinecartTurbo
{
    /// <summary>
    /// ECSManager.Init() enqueues Manager.mod.ExtraAuthoring for entity conversion. Registering our cloned cart
    /// prefabs right before it runs guarantees they are converted with everything else.
    /// </summary>
    [HarmonyPatch(typeof(ECSManager), "Init", new System.Type[0])]
    internal static class ECSManagerInitPatch
    {
        [HarmonyPrefix]
        private static void Prefix()
        {
            MinecartTurboRegistry.TryRegister("ECSManager.Init prefix");
        }
    }
}
