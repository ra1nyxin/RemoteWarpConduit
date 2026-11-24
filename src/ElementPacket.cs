using System;
using Newtonsoft.Json.Linq;

namespace RemoteWarpConduit
{
    // Element and disease identifiers are stable IDs, not per-session simulation indices.
    internal sealed class ElementPacket
    {
        internal string WorldId;
        internal string ReceiverId;
        internal string Phase;
        internal int ElementId;
        internal float Mass;
        internal float Temperature;
        internal string DiseaseId;
        internal int DiseaseCount;

        internal JObject ToJson()
        {
            Validate();
            return new JObject {
                ["world_id"] = WorldId, ["receiver_id"] = ReceiverId, ["phase"] = Phase,
                ["element_id"] = ElementId, ["mass"] = Mass, ["temperature"] = Temperature,
                ["disease_id"] = DiseaseId, ["disease_count"] = DiseaseCount
            };
        }

        internal static ElementPacket FromJson(JObject json)
        {
            if (json == null) throw new ArgumentException("Missing element packet.");
            ElementPacket packet = new ElementPacket {
                WorldId = (string)json["world_id"], ReceiverId = (string)json["receiver_id"],
                Phase = (string)json["phase"], ElementId = (int)json["element_id"],
                Mass = (float)json["mass"], Temperature = (float)json["temperature"],
                DiseaseId = (string)json["disease_id"], DiseaseCount = (int)json["disease_count"]
            };
            packet.Validate();
            return packet;
        }

        private void Validate()
        {
            if (string.IsNullOrEmpty(WorldId) || WorldId.Length > 256 || string.IsNullOrEmpty(ReceiverId) || ReceiverId.Length > 256)
                throw new ArgumentException("Invalid target.");
            if (Phase != "gas" && Phase != "liquid" && Phase != "solid")
                throw new ArgumentException("Invalid phase.");
            if (ElementId == 0 || float.IsNaN(Mass) || float.IsInfinity(Mass) || Mass <= 0f || Mass > 100000f)
                throw new ArgumentException("Invalid element or mass.");
            if (float.IsNaN(Temperature) || float.IsInfinity(Temperature) || Temperature < 0f || Temperature > 10000f)
                throw new ArgumentException("Invalid temperature.");
            if (DiseaseCount < 0 || (DiseaseCount > 0 && string.IsNullOrEmpty(DiseaseId)) || (DiseaseId != null && DiseaseId.Length > 256))
                throw new ArgumentException("Invalid disease.");
        }
    }
}
