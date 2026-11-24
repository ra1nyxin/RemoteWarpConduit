using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace RemoteWarpConduit
{
    [HarmonyPatch(typeof(SaveLoader), "Save", new Type[] { typeof(string), typeof(bool), typeof(bool) })]
    internal static class SaveConfirmationPatch
    {
        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            List<CodeInstruction> code = instructions.ToList();
            MethodInfo collect = AccessTools.Method(typeof(GC), "Collect", Type.EmptyTypes);
            MethodInfo confirm = AccessTools.Method(typeof(SaveConfirmationPatch), "AfterSuccessfulSave");
            List<int> matches = new List<int>();
            for (int i = 0; i < code.Count; i++)
                if (code[i].Calls(collect)) matches.Add(i);
            if (matches.Count != 1 || confirm == null)
            {
                Debug.LogError("[RemoteWarpConduit] Save confirmation hook not installed; received cargo remains queued until a saved game is reloaded.");
                return code;
            }
            int index = matches[0];
            CodeInstruction call = new CodeInstruction(OpCodes.Call, confirm);
            call.labels.AddRange(code[index].labels);
            code[index].labels.Clear();
            code.Insert(index, call);
            return code;
        }

        private static void AfterSuccessfulSave()
        {
            try
            {
                foreach (RemoteReceiverBuffers receiver in UnityEngine.Object.FindObjectsByType<RemoteReceiverBuffers>(FindObjectsSortMode.None))
                    receiver.Saved();
            }
            catch (Exception ex)
            {
                Debug.LogError("[RemoteWarpConduit] Could not confirm saved deliveries: " + ex);
            }
        }
    }
}
