using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace RemoteWarpConduit
{
    [HarmonyPatch(typeof(DetailsScreen), "OnPrefabInit")]
    internal static class RegisterRemoteSideScreen
    {
        private static void Postfix(DetailsScreen __instance)
        {
            try
            {
                FieldInfo field = AccessTools.Field(typeof(DetailsScreen), "sideScreens");
                List<DetailsScreen.SideScreenRef> screens = field != null ? field.GetValue(__instance) as List<DetailsScreen.SideScreenRef> : null;
                if (screens == null) { Debug.LogError("[RemoteWarpConduit] Could not find side screen list."); return; }
                if (screens.Exists(s => s != null && s.name == "RemoteWarpConduitSideScreen")) return;
                GameObject prefab = new GameObject("RemoteWarpConduitSideScreen");
                prefab.SetActive(false);
                prefab.AddComponent<RectTransform>();
                RemoteSideScreen screen = prefab.AddComponent<RemoteSideScreen>();
                screens.Add(new DetailsScreen.SideScreenRef { name = "RemoteWarpConduitSideScreen", screenPrefab = screen, tab = DetailsScreen.SidescreenTabTypes.Config });
                Debug.Log("[RemoteWarpConduit] Registered configuration side screen.");
            }
            catch (Exception ex) { Debug.LogError("[RemoteWarpConduit] Could not register configuration side screen: " + ex); }
        }
    }

    public sealed class RemoteSideScreen : SideScreenContent
    {
        private GameObject target;
        private RectTransform content;
        private Font font;
        private float nextRefresh;
        private string notice;
        private bool confirmClear;
        private JObject lastClearResult;

        public override bool IsValidForTarget(GameObject go)
        {
            return go != null && (go.GetComponent<WarpConduitSender>() != null || go.GetComponent<WarpConduitReceiver>() != null);
        }

        public override string GetTitle() { return "远程供给传送器"; }
        public override void SetTarget(GameObject go) { target = go; notice = null; confirmClear = false; EnsureBuilt(); Refresh(); }
        public override void ClearTarget() { target = null; }

        private void Update()
        {
            if (target != null && Time.unscaledTime >= nextRefresh) Refresh();
        }

        private void EnsureBuilt()
        {
            if (content != null) return;
            font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            LayoutElement layout = gameObject.AddComponent<LayoutElement>();
            layout.minHeight = 230f;
            layout.preferredHeight = 310f;
            GameObject view = new GameObject("Viewport", typeof(RectTransform), typeof(Image), typeof(Mask));
            view.transform.SetParent(transform, false);
            RectTransform viewport = view.GetComponent<RectTransform>();
            viewport.anchorMin = Vector2.zero; viewport.anchorMax = Vector2.one;
            viewport.offsetMin = Vector2.zero; viewport.offsetMax = Vector2.zero;
            view.GetComponent<Image>().color = new Color(1f, 1f, 1f, 0.01f);
            view.GetComponent<Mask>().showMaskGraphic = false;
            GameObject body = new GameObject("Content", typeof(RectTransform), typeof(VerticalLayoutGroup), typeof(ContentSizeFitter));
            body.transform.SetParent(view.transform, false);
            content = body.GetComponent<RectTransform>();
            content.anchorMin = new Vector2(0f, 1f); content.anchorMax = Vector2.one;
            content.pivot = new Vector2(0.5f, 1f); content.offsetMin = Vector2.zero; content.offsetMax = Vector2.zero;
            VerticalLayoutGroup group = body.GetComponent<VerticalLayoutGroup>();
            group.padding = new RectOffset(8, 8, 8, 8); group.spacing = 4f;
            group.childForceExpandHeight = false; group.childForceExpandWidth = true;
            body.GetComponent<ContentSizeFitter>().verticalFit = ContentSizeFitter.FitMode.PreferredSize;
            ScrollRect scroll = gameObject.AddComponent<ScrollRect>();
            scroll.horizontal = false; scroll.vertical = true; scroll.scrollSensitivity = 30f;
            scroll.movementType = ScrollRect.MovementType.Clamped; scroll.viewport = viewport; scroll.content = content;
            ContentContainer = body;
        }

        private void Refresh()
        {
            nextRefresh = Time.unscaledTime + 5f;
            for (int i = content.childCount - 1; i >= 0; i--)
            {
                GameObject child = content.GetChild(i).gameObject;
                child.SetActive(false);
                UnityEngine.Object.Destroy(child);
            }
            if (target == null) return;
            RemoteRuntime runtime = RemoteRuntime.Instance;
            RelayClient client = runtime != null ? runtime.Client : null;
            if (runtime != null && runtime.QueueClearResult != null && runtime.QueueClearResult != lastClearResult)
            {
                lastClearResult = runtime.QueueClearResult;
                notice = "服务器已清除 " + (int?)lastClearResult["cleared"] + " 条排队投递";
            }
            AddLabel(client != null && client.Connected ? "服务器已连接  " + (client.LatencyMs >= 0 ? client.LatencyMs + " ms" : "等待心跳") : "服务器未连接", 25);
            if (Time.timeScale <= 0f) AddLabel("游戏暂停：物资传输等待中", 25);
            if (!string.IsNullOrEmpty(notice)) AddLabel(notice, 40);
            RemoteSenderBuffers sender = target.GetComponent<RemoteSenderBuffers>();
            if (sender == null)
            {
                Activatable activation = target.GetComponent<Activatable>();
                AddLabel(activation != null && activation.IsActivated ? "输出端已激活，可接收远程物资" : "输出端未激活", 30);
                RemoteReceiverBuffers receiver = target.GetComponent<RemoteReceiverBuffers>();
                if (receiver != null)
                {
                    AddLabel("输出端: " + receiver.ReceiverId, 30);
                    AddLabel("本地缓冲 气/液/固: " + Mass(receiver.gas) + " / " + Mass(receiver.liquid) + " / " + Mass(receiver.solid) + " kg", 30);
                    AddLabel("已接收、等待存档确认: " + receiver.AwaitingSave + " / 256", 30);
                    ShowQueue(runtime, client, "receiver", client != null ? client.Id : null, receiver.WorldId, receiver.ReceiverId, receiver);
                }
                return;
            }
            AddLabel(sender.HasRemoteTarget ? "当前目标: " + sender.targetClient + " / " + sender.targetReceiver : "当前模式: 原版星球传送", 45);
            Activatable senderActivation = target.GetComponent<Activatable>();
            AddLabel(senderActivation != null && senderActivation.IsActivated ? "输入端已激活" : "输入端未激活", 25);
            AddLabel("本地缓冲 气/液/固: " + Mass(sender.gas) + " / " + Mass(sender.liquid) + " / " + Mass(sender.solid) + " kg", 30);
            AddLabel(string.IsNullOrEmpty(sender.pendingId) ? "待发送: 无" : "待发送: 1 条（等待服务器确认）", 30);
            if (sender.HasRemoteTarget) ShowQueue(runtime, client, "sender", sender.targetClient, sender.targetWorld, sender.targetReceiver, null);
            AddButton("切换到原版传送", delegate { Select(sender, null, null, null); });
            int count = 0;
            if (runtime != null && client != null)
            {
                foreach (JToken peer in runtime.Peers)
                {
                    string id = (string)peer["id"];
                    if (string.IsNullOrEmpty(id) || id == client.Id) continue;
                    JToken metadata = peer["metadata"];
                    JArray ports = metadata == null ? null : metadata["receivers"] as JArray;
                    if (ports == null) continue;
                    foreach (JToken port in ports)
                    {
                        string world = (string)port["world_id"], receiver = (string)port["receiver_id"];
                        if (string.IsNullOrEmpty(world) || string.IsNullOrEmpty(receiver)) continue;
                        string status = (bool?)peer["online"] == true ? "在线 " + ((string)peer["latency_ms"] ?? "--") + " ms" : "离线";
                        AddButton((string)peer["name"] + " / " + (string)metadata["colony_name"] + " / " + (string)port["world_name"] + "\n" + (string)peer["ip"] + "  " + receiver + "  " + status,
                            delegate { Select(sender, id, world, receiver); });
                        count++;
                    }
                }
            }
            if (count == 0) AddLabel("暂无其他玩家的输出端", 30);
        }

        private void Select(RemoteSenderBuffers sender, string id, string world, string receiver)
        {
            confirmClear = false;
            notice = sender.SelectTarget(id, world, receiver) ? "已切换传送目标" : "远程缓冲或待发送物资未清空，无法切换";
            Refresh();
        }

        private static string Mass(Storage storage) { return storage == null ? "--" : storage.MassStored().ToString("0.##"); }

        private void ShowQueue(RemoteRuntime runtime, RelayClient client, string role, string targetId, string world, string receiverId, RemoteReceiverBuffers receiver)
        {
            if (runtime == null || client == null || !client.Connected || string.IsNullOrEmpty(world) || string.IsNullOrEmpty(receiverId)) return;
            JObject request = new JObject { ["type"] = "queue_status", ["role"] = role, ["target"] = targetId, ["world_id"] = world, ["receiver_id"] = receiverId };
            client.Send(request);
            JObject status = runtime.QueueStatus;
            if (status == null || (string)status["role"] != role || (string)status["target"] != targetId || (string)status["world_id"] != world || (string)status["receiver_id"] != receiverId)
            {
                AddLabel("服务器队列: 查询中", 25);
                return;
            }
            JObject counts = status["counts"] as JObject;
            JArray ids = status["ids"] as JArray;
            if (counts == null || ids == null) return;
            AddLabel("服务器队列: " + (int?)status["total"] + " 条  气/液/固: " + (int?)counts["gas"] + " / " + (int?)counts["liquid"] + " / " + (int?)counts["solid"], 35);
            AddLabel("清除仅影响当前目标的服务器队列，已接收和本地缓冲不受影响。", 40);
            AddButton(confirmClear ? "确认清除当前服务器队列" : "清除服务器队列...", delegate {
                if (!confirmClear) { confirmClear = true; Refresh(); return; }
                confirmClear = false;
                JArray selected = receiver != null ? receiver.ClearableIds(ids) : new JArray(ids);
                if (receiver != null) receiver.BeginClear(selected);
                client.Send(new JObject { ["type"] = "queue_clear", ["role"] = role, ["target"] = targetId, ["world_id"] = world, ["receiver_id"] = receiverId, ["ids"] = selected });
                notice = "正在清除服务器队列";
                Refresh();
            });
        }

        private Text AddLabel(string value, float height)
        {
            GameObject go = new GameObject("Label", typeof(RectTransform), typeof(Text), typeof(LayoutElement));
            go.transform.SetParent(content, false);
            Text text = go.GetComponent<Text>(); text.font = font; text.fontSize = 14; text.color = Color.black;
            text.alignment = TextAnchor.MiddleLeft; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate; text.text = value;
            go.GetComponent<LayoutElement>().preferredHeight = height;
            return text;
        }

        private void AddButton(string value, System.Action action)
        {
            GameObject go = new GameObject("Target", typeof(RectTransform), typeof(Image), typeof(Button), typeof(LayoutElement));
            go.transform.SetParent(content, false); go.GetComponent<Image>().color = new Color(0.76f, 0.82f, 0.85f, 1f);
            go.GetComponent<LayoutElement>().preferredHeight = value.Contains("\n") ? 54f : 32f;
            Button button = go.GetComponent<Button>(); button.onClick.AddListener(delegate { action(); });
            GameObject label = new GameObject("Text", typeof(RectTransform), typeof(Text)); label.transform.SetParent(go.transform, false);
            RectTransform rect = label.GetComponent<RectTransform>(); rect.anchorMin = Vector2.zero; rect.anchorMax = Vector2.one;
            rect.offsetMin = new Vector2(6f, 2f); rect.offsetMax = new Vector2(-6f, -2f);
            Text text = label.GetComponent<Text>(); text.font = font; text.fontSize = 13; text.color = Color.black;
            text.alignment = TextAnchor.MiddleLeft; text.horizontalOverflow = HorizontalWrapMode.Wrap; text.verticalOverflow = VerticalWrapMode.Truncate; text.text = value;
        }
    }
}
