using System;
using UnityEngine;

namespace RemoteWarpConduit
{
    internal static class ElementCodec
    {
        internal static bool TryCapture(GameObject item, string colonyId, string receiverId, out ElementPacket packet)
        {
            packet = null;
            if (item == null) return false;
            PrimaryElement primary = item.GetComponent<PrimaryElement>();
            if (primary == null || primary.Element == null || primary.MassPerUnit != 1f) return false;
            Element element = primary.Element;
            string phase = element.IsGas ? "gas" : element.IsLiquid ? "liquid" : element.IsSolid ? "solid" : null;
            if (phase == null) return false;
            // Solid rail objects may be food, equipment, or seeds made of the same element.
            KPrefabID prefab = item.GetComponent<KPrefabID>();
            if (phase == "solid" && (prefab == null || prefab.PrefabTag != element.tag)) return false;
            int count = primary.DiseaseCount;
            string disease = null;
            if (count > 0)
            {
                byte index = primary.DiseaseIdx;
                if (index == byte.MaxValue) return false;
                disease = Db.Get().Diseases[index].Id;
                if (string.IsNullOrEmpty(disease)) return false;
            }
            packet = new ElementPacket {
                WorldId = colonyId, ReceiverId = receiverId, Phase = phase, ElementId = (int)element.id,
                Mass = primary.Mass, Temperature = primary.Temperature, DiseaseId = disease, DiseaseCount = count
            };
            try { packet.ToJson(); return true; }
            catch (ArgumentException) { packet = null; return false; }
        }

        internal static bool TryResolve(ElementPacket packet, out SimHashes id, out byte diseaseIndex)
        {
            id = (SimHashes)packet.ElementId;
            diseaseIndex = byte.MaxValue;
            Element element = ElementLoader.FindElementByHash(id);
            if (element == null) return false;
            if ((packet.Phase == "gas" && !element.IsGas) || (packet.Phase == "liquid" && !element.IsLiquid)
                || (packet.Phase == "solid" && !element.IsSolid)) return false;
            if (packet.DiseaseCount > 0)
            {
                if (!Db.Get().Diseases.IsValidID(packet.DiseaseId)) return false;
                diseaseIndex = Db.Get().Diseases.GetIndex(new HashedString(packet.DiseaseId));
                if (diseaseIndex == byte.MaxValue) return false;
            }
            return true;
        }
    }
}
