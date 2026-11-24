using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Newtonsoft.Json.Linq;

namespace RemoteWarpConduit
{
    // Network I/O never accesses Unity objects; callers exchange immutable JSON snapshots.
    internal sealed class RelayClient : IDisposable
    {
        private readonly string host;
        private readonly int port;
        private readonly string id;
        private readonly string name;
        private readonly ConcurrentQueue<string> outbound = new ConcurrentQueue<string>();
        private readonly ConcurrentQueue<JObject> inbound = new ConcurrentQueue<JObject>();
        private readonly Thread thread;
        private volatile bool stopping;
        private volatile bool connected;
        private volatile int latencyMs = -1;
        private TcpClient socket;

        internal RelayClient(string id, string name, string host = "43.143.112.84", int port = 53573)
        {
            this.id = id;
            this.name = name;
            this.host = host;
            this.port = port;
            thread = new Thread(Run) { IsBackground = true, Name = "RemoteWarpConduit relay" };
            thread.Start();
        }

        internal bool Connected { get { return connected; } }
        internal string Id { get { return id; } }
        internal int LatencyMs { get { return latencyMs; } }
        internal void Send(JObject message) { outbound.Enqueue(message.ToString(Newtonsoft.Json.Formatting.None)); }
        internal bool TryReceive(out JObject message) { return inbound.TryDequeue(out message); }

        private void Run()
        {
            int backoff = 1;
            while (!stopping)
            {
                try
                {
                    using (TcpClient client = new TcpClient())
                    {
                        socket = client;
                        IAsyncResult pending = client.BeginConnect(host, port, null, null);
                        if (!pending.AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(10)))
                            throw new IOException("Connection timed out.");
                        client.EndConnect(pending);
                        client.NoDelay = true;
                        using (NetworkStream stream = client.GetStream())
                        using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false), 4096, true) { AutoFlush = true })
                        {
                            stream.ReadTimeout = 15000;
                            writer.WriteLine(new JObject { ["type"] = "hello", ["id"] = id, ["name"] = name }.ToString(Newtonsoft.Json.Formatting.None));
                            StringBuilder responseBuffer = new StringBuilder();
                            JObject hello = JObject.Parse(ReadHandshake(stream, responseBuffer));
                            if ((string)hello["type"] != "hello" || (bool?)hello["ok"] != true)
                                throw new IOException("Relay rejected handshake.");
                            connected = true;
                            backoff = 1;
                            Stopwatch clock = Stopwatch.StartNew();
                            long lastPing = -60000;
                            long lastReceived = 0;
                            while (!stopping && client.Connected)
                            {
                                string line;
                                while (outbound.TryPeek(out line))
                                {
                                    writer.WriteLine(line);
                                    outbound.TryDequeue(out line);
                                }
                                if (clock.ElapsedMilliseconds - lastPing >= 60000)
                                {
                                    lastPing = clock.ElapsedMilliseconds;
                                    JObject ping = new JObject { ["type"] = "ping", ["nonce"] = lastPing };
                                    if (latencyMs >= 0) ping["latency_ms"] = latencyMs;
                                    writer.WriteLine(ping.ToString(Newtonsoft.Json.Formatting.None));
                                }
                                if (stream.DataAvailable)
                                {
                                    ReadResponses(stream, responseBuffer, clock);
                                    lastReceived = clock.ElapsedMilliseconds;
                                }
                                else if (client.Client.Poll(1000, SelectMode.SelectRead) && client.Available == 0)
                                    throw new IOException("Relay disconnected.");
                                else Thread.Sleep(25);
                                if (clock.ElapsedMilliseconds - lastReceived > 300000)
                                    throw new IOException("Relay heartbeat timed out.");
                            }
                        }
                    }
                }
                catch (Exception) { }
                finally { connected = false; latencyMs = -1; socket = null; }
                for (int i = 0; i < backoff * 10 && !stopping; i++) Thread.Sleep(100);
                backoff = Math.Min(60, backoff * 2);
            }
        }

        private static string ReadHandshake(NetworkStream stream, StringBuilder buffer)
        {
            while (true)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new IOException("Relay disconnected during handshake.");
                if (b == '\n')
                {
                    string line = buffer.ToString();
                    buffer.Clear();
                    return line;
                }
                if (b > 127 || buffer.Length >= 4096) throw new IOException("Invalid handshake.");
                buffer.Append((char)b);
            }
        }

        private void ReadResponses(NetworkStream stream, StringBuilder buffer, Stopwatch clock)
        {
            // Relay JSON is UTF-8; retain bytes until a complete line is available.
            while (stream.DataAvailable)
            {
                int b = stream.ReadByte();
                if (b < 0) throw new IOException("Relay disconnected.");
                if (b == '\n')
                {
                    byte[] bytes = new byte[buffer.Length];
                    for (int i = 0; i < bytes.Length; i++) bytes[i] = (byte)buffer[i];
                    buffer.Clear();
                    JObject message = JObject.Parse(Encoding.UTF8.GetString(bytes));
                    if ((string)message["type"] == "pong")
                        latencyMs = (int)Math.Min(int.MaxValue, Math.Max(0, clock.ElapsedMilliseconds - (long)message["nonce"]));
                    else inbound.Enqueue(message);
                }
                else
                {
                    if (buffer.Length >= 1048576) throw new IOException("Relay response too large.");
                    buffer.Append((char)b);
                }
            }
        }

        public void Dispose()
        {
            stopping = true;
            TcpClient current = socket;
            if (current != null) current.Close();
        }
    }
}
