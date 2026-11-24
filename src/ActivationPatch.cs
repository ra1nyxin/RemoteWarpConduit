using HarmonyLib;
using UnityEngine;

namespace RemoteWarpConduit
{
    [HarmonyPatch(typeof(WarpConduitSenderConfig), "DoPostConfigureComplete")]
    [HarmonyPriority(Priority.Last)]
    internal static class SenderActivationPatch
    {
        private static void Postfix(GameObject go)
        {
            ActivationPatch.RemoveSkillRequirement(go);
        }
    }

    [HarmonyPatch(typeof(WarpConduitReceiverConfig), "DoPostConfigureComplete")]
    [HarmonyPriority(Priority.Last)]
    internal static class ReceiverActivationPatch
    {
        private static void Postfix(GameObject go)
        {
            ActivationPatch.RemoveSkillRequirement(go);
        }
    }

    internal static class ActivationPatch
    {
        internal static void RemoveSkillRequirement(GameObject go)
        {
            Activatable activatable = go != null ? go.GetComponent<Activatable>() : null;
            if (activatable != null)
                activatable.requiredSkillPerk = null;
            else
                Debug.LogWarning("[RemoteWarpConduit] Activatable component missing; skill requirement not changed.");
        }
    }
}
