import asyncio
import json
import tempfile
import unittest
from pathlib import Path
from relay import Relay


class RelayTest(unittest.IsolatedAsyncioTestCase):
    async def asyncSetUp(self):
        self.tmp = tempfile.TemporaryDirectory()
        self.path = str(Path(self.tmp.name) / 'relay.db')
        self.relay = Relay(self.path)
        self.server = await asyncio.start_server(self.relay.handle, '127.0.0.1', 0)
        self.port = self.server.sockets[0].getsockname()[1]
        self.writers = []

    async def asyncTearDown(self):
        for writer in self.writers:
            writer.close()
            await writer.wait_closed()
        self.server.close()
        await self.server.wait_closed()
        await asyncio.sleep(0.05)
        self.relay.db.close()
        self.tmp.cleanup()

    async def client(self, name):
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        self.writers.append(writer)
        self.assertTrue((await self.call(reader, writer, {'type': 'hello', 'id': name, 'name': name}))['ok'])
        return reader, writer

    async def call(self, reader, writer, message):
        writer.write((json.dumps(message) + '\n').encode())
        await writer.drain()
        return json.loads(await reader.readline())

    async def test_queue_idempotency_and_ack(self):
        a, aw = await self.client('sender')
        b, bw = await self.client('receiver')
        request = {'type': 'send', 'delivery_id': 'one', 'target': 'receiver', 'payload': {'mass': 10, 'world_id': 'world', 'receiver_id': 'receiver'}}
        self.assertTrue((await self.call(a, aw, request))['ok'])
        self.assertTrue((await self.call(a, aw, request))['ok'])
        pending = await self.call(b, bw, {'type': 'pending', 'world_id': 'world', 'receiver_id': 'receiver'})
        self.assertEqual(len(pending['deliveries']), 1)
        self.assertTrue((await self.call(b, bw, {'type': 'ack', 'delivery_id': 'one'}))['ok'])
        self.assertTrue((await self.call(b, bw, {'type': 'ack', 'delivery_id': 'one'}))['ok'])
        self.assertEqual((await self.call(b, bw, {'type': 'pending', 'world_id': 'world', 'receiver_id': 'receiver'}))['deliveries'], [])
        self.assertEqual(self.relay.db.execute('SELECT COUNT(*) FROM deliveries').fetchone()[0], 1)

    async def test_offline_target_and_database_reopen(self):
        b, bw = await self.client('receiver')
        bw.close()
        await bw.wait_closed()
        await asyncio.sleep(0.05)
        a, aw = await self.client('sender')
        result = await self.call(a, aw, {'type': 'send', 'delivery_id': 'two', 'target': 'receiver', 'payload': {'mass': 1, 'world_id': 'world', 'receiver_id': 'receiver'}})
        self.assertTrue(result['ok'])
        other = Relay(self.path)
        self.assertEqual(other.db.execute('SELECT COUNT(*) FROM deliveries WHERE delivered IS NULL').fetchone()[0], 1)
        other.db.close()

    async def test_world_and_receiver_routing(self):
        a, aw = await self.client('sender')
        b, bw = await self.client('receiver')
        for item, world, receiver in [('one', 'world1', 'port1'), ('two', 'world2', 'port2')]:
            result = await self.call(a, aw, {'type': 'send', 'delivery_id': item, 'target': 'receiver',
                                             'payload': {'world_id': world, 'receiver_id': receiver}})
            self.assertTrue(result['ok'])
        request = {'type': 'pending', 'world_id': 'world2', 'receiver_id': 'port2'}
        self.assertEqual((await self.call(b, bw, request))['deliveries'][0]['id'], 'two')
        request['exclude'] = ['two']
        self.assertEqual((await self.call(b, bw, request))['deliveries'], [])
        request['exclude'] = []
        request['receiver_id'] = 'port1'
        self.assertEqual((await self.call(b, bw, request))['deliveries'], [])

    async def test_metadata_is_visible_and_persisted(self):
        a, aw = await self.client('sender')
        b, bw = await self.client('receiver')
        metadata = {'colony': 'colony', 'receivers': [{'world_id': 'world', 'receiver_id': 'port'}]}
        self.assertTrue((await self.call(b, bw, {'type': 'metadata', 'metadata': metadata}))['ok'])
        await self.call(b, bw, {'type': 'ping', 'nonce': 1, 'latency_ms': 27})
        clients = (await self.call(a, aw, {'type': 'clients'}))['clients']
        receiver = next(c for c in clients if c['id'] == 'receiver')
        self.assertEqual(receiver['metadata'], metadata)
        self.assertEqual(receiver['latency_ms'], 27)
        other = Relay(self.path)
        self.assertEqual(json.loads(other.db.execute('SELECT metadata FROM clients WHERE id=?', ('receiver',)).fetchone()[0]), metadata)
        other.db.close()

    async def test_phase_routing_skips_blocked_other_phase(self):
        a, aw = await self.client('sender')
        b, bw = await self.client('receiver')
        for delivery_id, phase in [('one', 'liquid'), ('two', 'gas')]:
            self.assertTrue((await self.call(a, aw, {'type': 'send', 'delivery_id': delivery_id, 'target': 'receiver',
                'payload': {'world_id': 'world', 'receiver_id': 'port', 'phase': phase}}))['ok'])
        pending = await self.call(b, bw, {'type': 'pending', 'world_id': 'world', 'receiver_id': 'port', 'phase': 'gas'})
        self.assertEqual(pending['deliveries'][0]['id'], 'two')


if __name__ == '__main__':
    unittest.main()
