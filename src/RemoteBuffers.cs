using HarmonyLib;
using KSerialization;
using Klei;
using System;
using System.Collections.Generic;
using Newtonsoft.Json.Linq;
using UnityEngine;

namespace RemoteWarpConduit
{
    [HarmonyPatch(typeof(WarpConduitSenderConfig), "ConfigureBuildingTemplate")]
    internal static class SenderBufferPatch
    {
        private static void Postfix(GameObject go)
        {
            RemoteSenderBuffers buffers = go.AddOrGet<RemoteSenderBuffers>();
            buffers.gas = AddBuffer(go, 1f);
            buffers.liquid = AddBuffer(go, 10f);
            buffers.solid = AddBuffer(go, 100f);
        }

        internal static Storage AddBuffer(GameObject go, float capacity)
        {
            Storage storage = go.AddComponent<Storage>();
            storage.capacityKg = capacity;
            storage.showInUI = false;
            return storage;
        }
    }

    [HarmonyPatch(typeof(WarpConduitSender), "OnSpawn")]
    internal static class SenderSpawnPatch
    {
        private static void Postfix(WarpConduitSender __instance)
        {
            RemoteSenderBuffers buffers = __instance.GetComponent<RemoteSenderBuffers>();
            if (buffers != null) buffers.RebindInputs();
        }
    }

    [HarmonyPatch(typeof(WarpConduitSender), "OnActivatedChanged")]
    internal static class SenderActivatedPatch
    {
        private static void Postfix(WarpConduitSender __instance)
        {
            RemoteSenderBuffers buffers = __instance.GetComponent<RemoteSenderBuffers>();
            if (buffers != null && buffers.HasRemoteTarget) buffers.RebindInputs();
        }
    }

    [HarmonyPatch(typeof(WarpConduitReceiverConfig), "ConfigureBuildingTemplate")]
    internal static class ReceiverBufferPatch
    {
        private static void Postfix(GameObject go)
        {
            RemoteReceiverBuffers buffers = go.AddOrGet<RemoteReceiverBuffers>();
            buffers.gas = SenderBufferPatch.AddBuffer(go, 1f);
            buffers.liquid = SenderBufferPatch.AddBuffer(go, 10f);
            buffers.solid = SenderBufferPatch.AddBuffer(go, 100f);
            ConduitDispenser gas = go.AddComponent<ConduitDispenser>();
            gas.conduitType = ConduitType.Gas;
            gas.useSecondaryOutput = true;
            gas.alwaysDispense = true;
            gas.storage = buffers.gas;
            buffers.gasDispenser = gas;
            ConduitDispenser liquid = go.AddComponent<ConduitDispenser>();
            liquid.conduitType = ConduitType.Liquid;
            liquid.useSecondaryOutput = true;
            liquid.alwaysDispense = true;
            liquid.storage = buffers.liquid;
            buffers.liquidDispenser = liquid;
            SolidConduitDispenser solid = go.AddComponent<SolidConduitDispenser>();
            solid.useSecondaryOutput = true;
            solid.alwaysDispense = true;
            solid.solidOnly = true;
            solid.storage = buffers.solid;
            buffers.solidDispenser = solid;
        }
    }

    [SerializationConfig(MemberSerialization.OptIn)]
    internal sealed class RemoteSenderBuffers : KMonoBehaviour
    {
        [SerializeField] internal Storage gas;
        [SerializeField] internal Storage liquid;
        [SerializeField] internal Storage solid;

        [Serialize] internal string targetClient;
        [Serialize] internal string targetWorld;
        [Serialize] internal string targetReceiver;
        [Serialize] internal string pendingId;
        [Serialize] internal string pendingPayload;

        private float nextRetry;
        private int nextPhase;

        protected override void OnSpawn()
        {
            base.OnSpawn();
            RemoteRuntime.RegisterSender(this);
            Subscribe((int)GameHashes.RefreshUserMenu, RefreshMenu);
        }

        protected override void OnCleanUp()
        {
            RemoteRuntime.UnregisterSender(this);
            base.OnCleanUp();
        }

        protected override void OnLoadLevel()
        {
            RemoteRuntime.UnregisterSender(this);
            base.OnLoadLevel();
        }

        private void RefreshMenu(object unused)
        {
            RemoteRuntime runtime = RemoteRuntime.Instance;
            if (Game.Instance == null || runtime == null || runtime.Client == null) return;
            Game.Instance.userMenu.AddButton(gameObject, new KIconButtonMenu.ButtonInfo("action_switch_toggle", "原版星球传送", delegate {
                if (!SelectTarget(null, null, null)) Debug.LogWarning("[RemoteWarpConduit] Drain pending remote cargo before changing targets.");
            }));
            foreach (JToken peer in runtime.Peers)
            {
                string client = (string)peer["id"];
                if (string.IsNullOrEmpty(client) || client == runtime.Client.Id) continue;
                JToken metadata = peer["metadata"];
                JArray ports = metadata == null ? null : metadata["receivers"] as JArray;
                if (ports == null) continue;
                foreach (JToken port in ports)
                {
                    string world = (string)port["world_id"];
                    string receiver = (string)port["receiver_id"];
                    if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(receiver)) continue;
                    string latency = peer["latency_ms"] == null || peer["latency_ms"].Type == JTokenType.Null ? "--" : peer["latency_ms"].ToString();
                    string status = (bool?)peer["online"] == true ? latency + " ms" : "离线";
                    string label = (string)peer["name"] + " / " + (string)metadata["colony_name"] + " / " + (string)port["world_name"];
                    string detail = (string)peer["ip"] + " · " + receiver + " · " + status;
                    Game.Instance.userMenu.AddButton(gameObject, new KIconButtonMenu.ButtonInfo("action_switch_toggle", label, delegate {
                        if (!SelectTarget(client, world, receiver)) Debug.LogWarning("[RemoteWarpConduit] Drain pending remote cargo before changing targets.");
                    }, tooltipText: detail));
                }
            }
        }

        internal bool HasRemoteTarget { get { return !string.IsNullOrEmpty(targetClient); } }

        internal bool SelectTarget(string client, string world, string receiver)
        {
            if (!string.IsNullOrEmpty(pendingId) || gas.MassStored() > 0f || liquid.MassStored() > 0f || solid.items.Count > 0)
                return false;
            targetClient = client;
            targetWorld = world;
            targetReceiver = receiver;
            RebindInputs();
            return true;
        }

        internal void RebindInputs()
        {
            WarpConduitSender sender = GetComponent<WarpConduitSender>();
            if (sender == null || sender.gasStorage == null) return;
            foreach (ConduitConsumer consumer in GetComponents<ConduitConsumer>())
            {
                if (consumer.conduitType == ConduitType.Gas)
                    consumer.storage = HasRemoteTarget ? gas : sender.gasStorage;
                else if (consumer.conduitType == ConduitType.Liquid)
                    consumer.storage = HasRemoteTarget ? liquid : sender.liquidStorage;
            }
            foreach (SolidConduitConsumer consumer in GetComponents<SolidConduitConsumer>())
                consumer.storage = HasRemoteTarget ? solid : sender.solidStorage;
            Operational operational = GetComponent<Operational>();
            if (HasRemoteTarget)
            {
                bool active = GetComponent<Activatable>().IsActivated;
                if (operational.GetFlag(WarpConduitStatus.warpConnectedFlag) != active)
                    operational.SetFlag(WarpConduitStatus.warpConnectedFlag, active);
            }
            else
                WarpConduitStatus.UpdateWarpConduitsOperational(gameObject, sender.receiver != null ? sender.receiver.gameObject : null);
        }

        internal void Tick(RelayClient client)
        {
            if (!HasRemoteTarget || client == null || !client.Connected || Time.timeScale <= 0f) return;
            if (string.IsNullOrEmpty(pendingId))
            {
                Storage[] buffers = { gas, liquid, solid };
                for (int i = 0; i < buffers.Length; i++)
                {
                    int index = (nextPhase + i) % buffers.Length;
                    if (TryStage(buffers[index]))
                    {
                        nextPhase = (index + 1) % buffers.Length;
                        break;
                    }
                }
            }
            if (!string.IsNullOrEmpty(pendingId) && Time.unscaledTime >= nextRetry)
            {
                nextRetry = Time.unscaledTime + 5f;
                client.Send(new JObject { ["type"] = "send", ["delivery_id"] = pendingId,
                    ["target"] = targetClient, ["payload"] = JObject.Parse(pendingPayload) });
            }
        }

        private bool TryStage(Storage storage)
        {
            if (storage == null) return false;
            foreach (GameObject item in storage.items.ToArray())
            {
                ElementPacket packet;
                if (!ElementCodec.TryCapture(item, targetWorld, targetReceiver, out packet)) continue;
                string id = Guid.NewGuid().ToString("D");
                float maximum = packet.Phase == "gas" ? 1f : packet.Phase == "liquid" ? 10f : 100f;
                if (packet.Mass > maximum)
                {
                    PrimaryElement primary = item.GetComponent<PrimaryElement>();
                    float consumed;
                    float temperature;
                    SimUtil.DiseaseInfo disease;
                    storage.ConsumeAndGetDisease(primary.Element.tag, maximum, out consumed, out disease, out temperature);
                    if (consumed <= 0f) continue;
                    packet.Mass = consumed;
                    packet.Temperature = temperature;
                    packet.DiseaseCount = disease.count;
                    packet.DiseaseId = disease.count > 0 ? Db.Get().Diseases[disease.idx].Id : null;
                }
                else storage.ConsumeIgnoringDisease(item);
                pendingPayload = packet.ToJson().ToString(Newtonsoft.Json.Formatting.None);
                pendingId = id;
                nextRetry = 0f;
                return true;
            }
            return false;
        }

        internal void HandleSendResult(JObject message)
        {
            if ((string)message["id"] == pendingId && (bool?)message["ok"] == true)
            {
                pendingId = null;
                pendingPayload = null;
            }
        }
    }

    [SerializationConfig(MemberSerialization.OptIn)]
    internal sealed class RemoteReceiverBuffers : KMonoBehaviour
    {
        [SerializeField] internal Storage gas;
        [SerializeField] internal Storage liquid;
        [SerializeField] internal Storage solid;
        [SerializeField] internal ConduitDispenser gasDispenser;
        [SerializeField] internal ConduitDispenser liquidDispenser;
        [SerializeField] internal SolidConduitDispenser solidDispenser;
        [Serialize] private List<string> applied = new List<string>();
        private HashSet<string> durable = new HashSet<string>();
        private HashSet<string> appliedLookup = new HashSet<string>();
        private const int MaxUnconfirmed = 256;
        private const int MaxAckPerPoll = 32;
        private float nextPoll;

        protected override void OnSpawn()
        {
            base.OnSpawn();
            gasDispenser.storage = gas;
            liquidDispenser.storage = liquid;
            solidDispenser.storage = solid;
            durable = new HashSet<string>(applied);
            appliedLookup = new HashSet<string>(applied);
            RemoteRuntime.RegisterReceiver(this);
        }

        protected override void OnCleanUp()
        {
            RemoteRuntime.UnregisterReceiver(this);
            base.OnCleanUp();
        }

        protected override void OnLoadLevel()
        {
            RemoteRuntime.UnregisterReceiver(this);
            base.OnLoadLevel();
        }

        internal string WorldId { get { return SaveLoader.Instance.GameInfo.colonyGuid.ToString("D"); } }
        internal string ReceiverId { get { return this.GetMyWorldId() + ":" + Grid.PosToCell(this); } }

        internal void Tick(RelayClient client)
        {
            if (client == null || !client.Connected || Time.timeScale <= 0f || !GetComponent<Activatable>().IsActivated) return;
            if (Time.unscaledTime < nextPoll) return;
            nextPoll = Time.unscaledTime + 3f;
            int sent = 0;
            foreach (string id in durable)
            {
                client.Send(new JObject { ["type"] = "ack", ["delivery_id"] = id });
                if (++sent >= MaxAckPerPoll) break;
            }
            if (applied.Count >= MaxUnconfirmed) return;
            foreach (string phase in new[] { "gas", "liquid", "solid" })
                client.Send(new JObject { ["type"] = "pending", ["world_id"] = WorldId,
                    ["receiver_id"] = ReceiverId, ["phase"] = phase, ["exclude"] = new JArray(applied) });
        }

        internal void Receive(string id, ElementPacket packet)
        {
            if (Time.timeScale <= 0f || !GetComponent<Activatable>().IsActivated || applied.Count >= MaxUnconfirmed || appliedLookup.Contains(id)) return;
            if (packet.WorldId != WorldId || packet.ReceiverId != ReceiverId) return;
            SimHashes element;
            byte disease;
            if (!ElementCodec.TryResolve(packet, out element, out disease)) return;
            Storage buffer = packet.Phase == "gas" ? gas : packet.Phase == "liquid" ? liquid : solid;
            if (buffer == null || buffer.RemainingCapacity() < packet.Mass) return;
            buffer.AddElement(element, packet.Mass, packet.Temperature, disease, packet.DiseaseCount, false, false);
            applied.Add(id);
            appliedLookup.Add(id);
        }

        internal void Saved()
        {
            foreach (string id in applied) durable.Add(id);
            nextPoll = 0f;
        }

        internal void Acknowledged(string id)
        {
            if (durable.Remove(id)) { applied.Remove(id); appliedLookup.Remove(id); }
        }
    }

    [HarmonyPatch(typeof(ConduitDispenser), "ConduitUpdate")]
    internal static class RemoteConduitDispenserPatch
    {
        private static bool Prefix(ConduitDispenser __instance)
        {
            RemoteReceiverBuffers remote = __instance.GetComponent<RemoteReceiverBuffers>();
            return remote == null || (__instance.storage != remote.gas && __instance.storage != remote.liquid)
                || __instance.GetComponent<Activatable>().IsActivated;
        }
    }

    [HarmonyPatch(typeof(SolidConduitDispenser), "ConduitUpdate")]
    internal static class RemoteSolidDispenserPatch
    {
        private static bool Prefix(SolidConduitDispenser __instance)
        {
            RemoteReceiverBuffers remote = __instance.GetComponent<RemoteReceiverBuffers>();
            return remote == null || __instance.storage != remote.solid || __instance.GetComponent<Activatable>().IsActivated;
        }
    }
}
