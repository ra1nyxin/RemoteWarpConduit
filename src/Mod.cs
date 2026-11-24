using System;
using System.IO;
using System.Collections.Generic;
using HarmonyLib;
using KMod;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RemoteWarpConduit
{
    public sealed class Mod : UserMod2
    {
        public override void OnLoad(Harmony harmony)
        {
            base.OnLoad(harmony);
            Debug.Log("[RemoteWarpConduit] Loaded.");
        }
    }

    [HarmonyPatch(typeof(Game), "OnSpawn")]
    internal static class GameSpawnPatch
    {
        private static void Postfix(Game __instance)
        {
            if (__instance.GetComponent<RemoteRuntime>() == null)
                __instance.gameObject.AddComponent<RemoteRuntime>();
        }
    }

    internal sealed class RemoteRuntime : MonoBehaviour
    {
        internal static RemoteRuntime Instance;
        private static readonly List<RemoteSenderBuffers> senders = new List<RemoteSenderBuffers>();
        private static readonly List<RemoteReceiverBuffers> receivers = new List<RemoteReceiverBuffers>();
        internal static void RegisterSender(RemoteSenderBuffers sender) { if (!senders.Contains(sender)) senders.Add(sender); }
        internal static void UnregisterSender(RemoteSenderBuffers sender) { senders.Remove(sender); }
        internal static void RegisterReceiver(RemoteReceiverBuffers receiver) { if (!receivers.Contains(receiver)) receivers.Add(receiver); }
        internal static void UnregisterReceiver(RemoteReceiverBuffers receiver) { receivers.Remove(receiver); }
        internal RelayClient Client { get; private set; }
        internal JArray Peers { get; private set; } = new JArray();
        private float nextRefresh;

        private void Awake()
        {
            Instance = this;
            try
            {
                string path = Path.Combine(Application.persistentDataPath, "RemoteWarpConduit-client-id.txt");
                string id = File.Exists(path) ? File.ReadAllText(path).Trim() : null;
                Guid parsed;
                if (!Guid.TryParse(id, out parsed))
                {
                    id = Guid.NewGuid().ToString("D");
                    File.WriteAllText(path, id);
                }
                Client = new RelayClient(id, Environment.MachineName);
            }
            catch (Exception ex)
            {
                Debug.LogError("[RemoteWarpConduit] Failed to initialize client identity: " + ex);
            }
        }

        private void Update()
        {
            if (Client == null) return;
            JObject message;
            while (Client.TryReceive(out message))
            {
                if ((string)message["type"] == "clients" && message["clients"] is JArray)
                    Peers = (JArray)message["clients"];
                else if ((string)message["type"] == "send")
                    foreach (RemoteSenderBuffers sender in senders)
                        sender.HandleSendResult(message);
                else if ((string)message["type"] == "pending" && message["deliveries"] is JArray)
                {
                    foreach (JObject delivery in (JArray)message["deliveries"])
                    {
                        try
                        {
                            ElementPacket packet = ElementPacket.FromJson((JObject)delivery["payload"]);
                            foreach (RemoteReceiverBuffers receiver in receivers)
                                receiver.Receive((string)delivery["id"], packet);
                        }
                        catch (Exception ex) { Debug.LogWarning("[RemoteWarpConduit] Invalid incoming cargo: " + ex.Message); }
                    }
                }
                else if ((string)message["type"] == "ack" && (bool?)message["ok"] == true)
                    foreach (RemoteReceiverBuffers receiver in receivers)
                        receiver.Acknowledged((string)message["id"]);
            }
            foreach (RemoteSenderBuffers sender in senders)
                sender.Tick(Client);
            foreach (RemoteReceiverBuffers receiver in receivers)
                receiver.Tick(Client);
            if (!Client.Connected || Time.unscaledTime < nextRefresh || SaveLoader.Instance == null || SaveGame.Instance == null)
                return;
            nextRefresh = Time.unscaledTime + 10f;
            try
            {
                string colonyId = SaveLoader.Instance.GameInfo.colonyGuid.ToString("D");
                JArray receivers = new JArray();
                foreach (WarpConduitReceiver receiver in UnityEngine.Object.FindObjectsByType<WarpConduitReceiver>(FindObjectsSortMode.None))
                {
                    WorldContainer world = receiver.GetMyWorld();
                    if (world == null) continue;
                    int cell = Grid.PosToCell(receiver);
                    receivers.Add(new JObject {
                        ["world_id"] = colonyId,
                        ["receiver_id"] = world.id + ":" + cell,
                        ["world_name"] = world.GetProperName(),
                        ["world_index"] = world.id,
                        ["activated"] = receiver.GetComponent<Activatable>().IsActivated
                    });
                }
                Client.Send(new JObject { ["type"] = "metadata", ["metadata"] = new JObject {
                    ["colony_id"] = colonyId, ["colony_name"] = SaveGame.Instance.BaseName, ["receivers"] = receivers
                } });
                Client.Send(new JObject { ["type"] = "clients" });
            }
            catch (Exception ex)
            {
                Debug.LogWarning("[RemoteWarpConduit] Could not publish receiver information: " + ex.Message);
            }
        }

        private void OnDestroy()
        {
            if (Client != null) Client.Dispose();
            if (Instance == this) Instance = null;
        }
    }
}
