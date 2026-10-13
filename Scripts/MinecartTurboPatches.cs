using HarmonyLib;
using UnityEngine;

namespace MinecartTurbo
{
    /// <summary>
    /// ECSManager.Init() converts every EntityAuthoringDataBlock into entities. Attaching our cloned cart prefabs
    /// to their data blocks right before it runs guarantees they are converted with everything else.
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

    /// <summary>
    /// The carts pin ObjectIDs 32768-32770 (the IDs they have held since 1.0.0). The game starts handing out
    /// sequential IDs to unpinned mod objects at exactly 32768, so without this any other mod's first unpinned
    /// object would collide with Turbo Minecart (4x). Start the sequential counter just above our range instead.
    /// </summary>
    [HarmonyPatch(typeof(ECSManager), "CalculateReservedObjectIndices")]
    internal static class ReservedObjectIndicesPatch
    {
        private static bool _logged;

        [HarmonyPostfix]
        private static void Postfix(ref int __result)
        {
            if (__result <= MinecartTurboRegistry.LastReservedID)
            {
                __result = MinecartTurboRegistry.LastReservedID + 1;
            }
            if (!_logged)
            {
                _logged = true;
                Debug.Log("[MinecartTurbo] sequential mod ObjectIDs start at " + __result + " (carts hold "
                          + MinecartTurboRegistry.FirstReservedID + "-" + MinecartTurboRegistry.LastReservedID + ").");
            }
        }
    }
}
