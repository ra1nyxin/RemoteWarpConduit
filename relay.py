#!/usr/bin/env python3
"""RemoteWarpConduit relay prototype. JSON lines over TCP; SQLite persistence."""
import argparse
import asyncio
import json
import logging
import sqlite3
import time
from pathlib import Path

PORT = 53573
MAX_LINE = 1024 * 1024
MAX_PENDING = 1000
TIMEOUT = 300
LOG = logging.getLogger("remote-warp")


class Relay:
    def __init__(self, db_path):
        self.db = sqlite3.connect(db_path)
        self.db.execute("PRAGMA journal_mode=WAL")
        self.db.execute("PRAGMA synchronous=FULL")
        self.db.executescript("""
            CREATE TABLE IF NOT EXISTS clients (
                id TEXT PRIMARY KEY, name TEXT NOT NULL, ip TEXT NOT NULL,
                metadata TEXT NOT NULL, last_seen REAL NOT NULL
            );
            CREATE TABLE IF NOT EXISTS deliveries (
                id TEXT PRIMARY KEY, sender TEXT NOT NULL, target TEXT NOT NULL,
                payload TEXT NOT NULL, created REAL NOT NULL, delivered REAL
            );
            CREATE INDEX IF NOT EXISTS pending ON deliveries(target, delivered, created);
        """)
        self.db.commit()
        self.online = {}
        self.latencies = {}

    @staticmethod
    def field(message, name, limit=256):
        value = message.get(name)
        if not isinstance(value, str) or not value or len(value) > limit:
            raise ValueError("invalid " + name)
        return value

    async def send(self, writer, message):
        writer.write((json.dumps(message, ensure_ascii=False, separators=(",", ":")) + "\n").encode("utf-8"))
        await writer.drain()

    async def handle(self, reader, writer):
        client_id = None
        peer = writer.get_extra_info("peername")
        ip = str(peer[0]) if peer else "unknown"
        try:
            while True:
                line = await asyncio.wait_for(reader.readline(), TIMEOUT)
                if not line:
                    break
                message = json.loads(line)
                if not isinstance(message, dict):
                    raise ValueError("message must be an object")
                kind = message.get("type")
                if kind == "hello":
                    if client_id is not None:
                        raise ValueError("already registered")
                    client_id = self.field(message, "id")
                    name = self.field(message, "name")
                    metadata = message.get("metadata", {})
                    if not isinstance(metadata, dict):
                        raise ValueError("invalid metadata")
                    previous = self.online.get(client_id)
                    if previous is not None:
                        previous.close()
                    self.online[client_id] = writer
                    self.db.execute("INSERT OR REPLACE INTO clients VALUES (?, ?, ?, ?, ?)",
                                    (client_id, name, ip, json.dumps(metadata, ensure_ascii=False), time.time()))
                    self.db.commit()
                    LOG.info("online id=%s name=%s ip=%s", client_id, name, ip)
                    await self.send(writer, {"type": "hello", "ok": True})
                elif client_id is None:
                    raise ValueError("hello required")
                elif kind == "ping":
                    latency = message.get("latency_ms")
                    if isinstance(latency, (int, float)) and not isinstance(latency, bool) and 0 <= latency <= 300000:
                        self.latencies[client_id] = latency
                    self.db.execute("UPDATE clients SET last_seen=? WHERE id=?", (time.time(), client_id))
                    self.db.commit()
                    await self.send(writer, {"type": "pong", "nonce": message.get("nonce")})
                elif kind == "metadata":
                    metadata = message.get("metadata")
                    if not isinstance(metadata, dict):
                        raise ValueError("invalid metadata")
                    self.db.execute("UPDATE clients SET metadata=?, last_seen=? WHERE id=?",
                                    (json.dumps(metadata, ensure_ascii=False), time.time(), client_id))
                    self.db.commit()
                    await self.send(writer, {"type": "metadata", "ok": True})
                elif kind == "clients":
                    rows = self.db.execute("SELECT id,name,ip,metadata,last_seen FROM clients ORDER BY name,id").fetchall()
                    clients = [{"id": row[0], "name": row[1], "ip": row[2], "metadata": json.loads(row[3]),
                                "last_seen": row[4], "online": row[0] in self.online,
                                "latency_ms": self.latencies.get(row[0]) if row[0] in self.online else None} for row in rows]
                    await self.send(writer, {"type": "clients", "clients": clients})
                elif kind == "send":
                    delivery_id = self.field(message, "delivery_id")
                    target = self.field(message, "target")
                    payload = message.get("payload")
                    if not isinstance(payload, dict):
                        raise ValueError("invalid payload")
                    self.field(payload, "world_id")
                    self.field(payload, "receiver_id")
                    encoded = json.dumps(payload, ensure_ascii=False, separators=(",", ":"))
                    old = self.db.execute("SELECT sender,target,payload FROM deliveries WHERE id=?", (delivery_id,)).fetchone()
                    if old:
                        if old != (client_id, target, encoded):
                            raise ValueError("delivery ID collision")
                    else:
                        if not self.db.execute("SELECT 1 FROM clients WHERE id=?", (target,)).fetchone():
                            raise ValueError("unknown target")
                        count = self.db.execute("SELECT COUNT(*) FROM deliveries WHERE target=? AND delivered IS NULL", (target,)).fetchone()[0]
                        if count >= MAX_PENDING:
                            await self.send(writer, {"type": "send", "id": delivery_id, "ok": False, "error": "queue_full"})
                            continue
                        self.db.execute("INSERT INTO deliveries(id,sender,target,payload,created) VALUES (?,?,?,?,?)",
                                        (delivery_id, client_id, target, encoded, time.time()))
                        self.db.commit()
                        LOG.info("queued id=%s sender=%s target=%s payload=%s", delivery_id, client_id, target, encoded)
                    await self.send(writer, {"type": "send", "id": delivery_id, "ok": True})
                elif kind == "pending":
                    world_id = self.field(message, "world_id")
                    receiver_id = self.field(message, "receiver_id")
                    phase = message.get("phase")
                    if phase is not None and phase not in ("gas", "liquid", "solid"):
                        raise ValueError("invalid phase")
                    excluded = message.get("exclude", [])
                    if not isinstance(excluded, list) or len(excluded) > MAX_PENDING or any(not isinstance(x, str) or len(x) > 256 for x in excluded):
                        raise ValueError("invalid exclusions")
                    excluded = set(excluded)
                    rows = self.db.execute("SELECT id,sender,payload FROM deliveries WHERE target=? AND delivered IS NULL ORDER BY created", (client_id,)).fetchall()
                    deliveries = []
                    for delivery_id, sender, encoded in rows:
                        payload = json.loads(encoded)
                        if (delivery_id not in excluded and payload.get("world_id") == world_id
                                and payload.get("receiver_id") == receiver_id
                                and (phase is None or payload.get("phase") == phase)):
                            deliveries.append({"id": delivery_id, "sender": sender, "payload": payload})
                            break
                    await self.send(writer, {"type": "pending", "deliveries": deliveries})
                elif kind == "ack":
                    delivery_id = self.field(message, "delivery_id")
                    row = self.db.execute("SELECT delivered FROM deliveries WHERE id=? AND target=?", (delivery_id, client_id)).fetchone()
                    if row is None:
                        raise ValueError("unknown delivery")
                    self.db.execute("UPDATE deliveries SET delivered=COALESCE(delivered, ?) WHERE id=? AND target=?", (time.time(), delivery_id, client_id))
                    self.db.commit()
                    LOG.info("delivered id=%s target=%s", delivery_id, client_id)
                    await self.send(writer, {"type": "ack", "id": delivery_id, "ok": True})
                else:
                    raise ValueError("unknown message type")
        except (ValueError, UnicodeError, json.JSONDecodeError, asyncio.LimitOverrunError) as exc:
            LOG.warning("protocol error peer=%s error=%s", ip, exc)
        except (ConnectionError, asyncio.TimeoutError, asyncio.IncompleteReadError):
            pass
        finally:
            if client_id is not None and self.online.get(client_id) is writer:
                del self.online[client_id]
                self.latencies.pop(client_id, None)
                self.db.execute("UPDATE clients SET last_seen=? WHERE id=?", (time.time(), client_id))
                self.db.commit()
                LOG.info("offline id=%s", client_id)
            writer.close()
            try:
                await writer.wait_closed()
            except ConnectionError:
                pass


async def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--host", default="0.0.0.0")
    parser.add_argument("--port", type=int, default=PORT)
    parser.add_argument("--database", default=str(Path(__file__).resolve().with_name("remote_warp.sqlite3")))
    args = parser.parse_args()
    relay = Relay(args.database)
    server = await asyncio.start_server(relay.handle, args.host, args.port, limit=MAX_LINE)
    LOG.info("listening on %s:%s database=%s", args.host, args.port, args.database)
    async with server:
        await server.serve_forever()


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
    try:
        asyncio.run(main())
    except KeyboardInterrupt:
        LOG.info("stopped")
