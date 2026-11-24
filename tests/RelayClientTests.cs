using System;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;
using RemoteWarpConduit;

internal static class RelayClientTests
{
    private static int Main()
    {
        ElementPacket packet = new ElementPacket { WorldId = "colony", ReceiverId = "world:1", Phase = "solid", ElementId = 123,
            Mass = 20f, Temperature = 298.15f, DiseaseId = "FoodPoisoning", DiseaseCount = 50 };
        ElementPacket decoded = ElementPacket.FromJson(packet.ToJson());
        if (decoded.WorldId != packet.WorldId || decoded.ElementId != 123 || decoded.DiseaseId != "FoodPoisoning" || decoded.Mass != 20f)
            throw new Exception("Element packet round trip failed");
        packet.Mass = float.NaN;
        bool rejected = false;
        try { packet.ToJson(); } catch (ArgumentException) { rejected = true; }
        if (!rejected) throw new Exception("Invalid mass accepted");
        TcpListener listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        Exception failure = null;
        Thread server = new Thread(() => {
            try {
                using (TcpClient socket = listener.AcceptTcpClient())
                using (NetworkStream stream = socket.GetStream())
                using (StreamReader input = new StreamReader(stream, Encoding.UTF8, false, 4096, true)) {
                    if (input.ReadLine() == null) throw new Exception("Missing handshake");
                    byte[] response = Encoding.UTF8.GetBytes("{\"type\":\"hello\",\"ok\":true}\n{\"type\":\"first\"}\n{\"type\":\"second\",\"name\":\"\u96e8\"}\n");
                    stream.Write(response, 0, response.Length);
                    Thread.Sleep(500);
                }
            } catch (Exception ex) { failure = ex; }
        });
        server.Start();
        using (RelayClient client = new RelayClient("test", "test", "127.0.0.1", port)) {
            string first = null, second = null;
            DateTime until = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < until && second == null) {
                JObject message;
                if (client.TryReceive(out message)) {
                    if (first == null) first = (string)message["type"];
                    else { second = (string)message["type"]; if ((string)message["name"] != "\u96e8") throw new Exception("UTF-8 corrupted"); }
                } else Thread.Sleep(10);
            }
            if (first != "first" || second != "second") throw new Exception("Combined responses were not received");
        }
        server.Join();
        listener.Stop();
        if (failure != null) throw failure;
        Console.WriteLine("Relay client tests passed.");
        return 0;
    }
}
