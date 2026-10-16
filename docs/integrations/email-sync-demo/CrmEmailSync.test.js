'use strict';
const { test, after, beforeEach } = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto'), https = require('node:https');
const { EventEmitter } = require('node:events');
const { CrmEmailSync, profileFor } = require('./CrmEmailSync.node');
const primary = crypto.randomUUID(), secondary = crypto.randomUUID();
const incoming = crypto.randomBytes(32), outgoing = crypto.randomBytes(32);
const credentials = { incomingId: 'synthetic-in', incomingSecret: incoming.toString('base64'),
  outgoingId: 'synthetic-out', outgoingSecret: outgoing.toString('base64'), caPem: 'synthetic-ca' };
const sign = (key, audience, id, time, path, body) => crypto.createHmac('sha256', key)
  .update(['crm-hmac-v1', audience, id, time, 'POST', path, crypto.createHash('sha256').update(body).digest('hex')].join('\n')).digest('hex');
let callbacks, status, tls;
beforeEach(() => { callbacks = []; status = 202; tls = true; });
const original = https.request;
https.request = (options, callback) => {
  assert.equal(options.hostname, 'localhost'); assert.equal(options.port, 7096);
  assert.equal(options.rejectUnauthorized, true); assert.equal(options.ca, credentials.caPem);
  assert.equal(options.minVersion, 'TLSv1.2'); assert.equal(options.path, '/api/integrations/results');
  const req = new EventEmitter();
  req.end = body => {
    assert.equal(options.headers['Content-Length'], body.length);
    assert.equal(options.headers['X-CRM-Signature'], sign(outgoing, 'n8n-to-crm', credentials.outgoingId,
      options.headers['X-CRM-Timestamp'], options.path, body));
    callbacks.push(JSON.parse(body));
    setImmediate(() => { const res = new EventEmitter(); res.statusCode = status; res.socket = { authorized: tls };
      callback(res); res.emit('data', Buffer.from(JSON.stringify({ status: 'received', alreadyRecorded: false }))); res.emit('end'); });
  };
  return req;
};
after(() => { https.request = original; });
function command(cursor = null, account = primary, maxItems = 10) {
  return { version: 1, messageId: crypto.randomUUID(), operationId: crypto.randomUUID(), correlationId: crypto.randomUUID(), type: 'email.sync',
    payload: { accountRef: account, previousCursor: cursor, maxItems } };
}
async function invoke(value, { raw = Buffer.from(JSON.stringify(value)), bad = false, expired = false, changed = false } = {}) {
  const time = String(Math.floor(Date.now() / 1000) - (expired ? 301 : 0));
  const headers = { 'content-type': 'application/json', 'x-crm-key-id': credentials.incomingId, 'x-crm-timestamp': time,
    'x-crm-signature': bad ? '0'.repeat(64) : sign(incoming, 'crm-to-n8n', credentials.incomingId, time, '/webhook/crm-email-sync-demo', raw) };
  const context = { getInputData: () => [{ json: { headers }, binary: { data: {} } }], getCredentials: async () => credentials,
    getNodeParameter: name => name === 'primaryAccountRef' ? primary : secondary,
    helpers: { getBinaryDataBuffer: async () => changed ? Buffer.concat([raw, Buffer.from(' ')]) : raw } };
  return (await new CrmEmailSync().execute.call(context))[0][0].json;
}
test('both lots preserve identical content and stable dates; shared HMAC signs the raw callback', async () => {
  const first = command(); assert.equal((await invoke(first)).responseCode, 202);
  const a = callbacks[0]; assert.equal(a.commandMessageId, first.messageId); assert.equal(a.operationId, first.operationId);
  assert.equal(a.correlationId, first.correlationId); assert.equal(a.payload.accountRef, primary);
  assert.deepEqual(a.payload.messages.map(m => m.externalMessageId), ['mail-demo-001', 'mail-demo-002']);
  await invoke(command('cursor-001')); const b = callbacks[1];
  assert.deepEqual(b.payload.messages[0], a.payload.messages[0]); assert.equal(b.payload.messages[1].externalMessageId, 'mail-demo-003');
  assert.equal(a.payload.nextCursor, 'cursor-001'); assert.equal(b.payload.nextCursor, 'cursor-002');
  assert.equal(a.payload.messages[0].occurredAt, '2026-10-16T08:01:00Z');
});
test('empty success advances the fixture cursor', async () => {
  await invoke(command('cursor-002', primary, 1));
  assert.deepEqual(callbacks[0].payload, { accountRef: primary, outcome: 'succeeded', nextCursor: 'cursor-003', messages: [] });
});
test('conflict deliberately changes content under the same identity', async () => {
  await invoke(command()); await invoke(command('cursor-003', primary, 1));
  const a = callbacks[0].payload.messages[0], b = callbacks[1].payload.messages[0];
  assert.equal(a.externalMessageId, b.externalMessageId); assert.notEqual(a.bodyText, b.bodyText);
  assert.equal(callbacks[1].payload.nextCursor, 'cursor-004');
});
test('second account has independent contents and cursor', async () => {
  await invoke(command(null, secondary, 1)); assert.equal(callbacks[0].payload.accountRef, secondary);
  assert.deepEqual(callbacks[0].payload.messages[0].toAddresses, ['demo-inbox-2@example.test']);
  assert.equal(callbacks[0].payload.messages[0].externalMessageId, 'mail-demo-001');
  await invoke(command('cursor-001', secondary, 1)); assert.deepEqual(callbacks[1].payload.messages, []);
});
for (const mode of ['bad', 'expired', 'changed']) test(`reject ${mode} signature without callback`, async () => {
  assert.equal((await invoke(command(), { [mode]: true })).responseCode, 401); assert.equal(callbacks.length, 0);
});
for (const [name, change] of [
  ['unknown field', p => { p.unknown = true; }], ['numeric cursor', p => { p.previousCursor = 1; }],
  ['unknown cursor', p => { p.previousCursor = 'unknown'; }], ['missing cursor', p => { delete p.previousCursor; }],
  ['other account', p => { p.accountRef = crypto.randomUUID(); }], ['empty account', p => { p.accountRef = ''; }],
  ['zero maxItems', p => { p.maxItems = 0; }], ['oversized maxItems', p => { p.maxItems = 101; }],
  ['fractional maxItems', p => { p.maxItems = 2.5; }], ['insufficient maxItems', p => { p.maxItems = 1; }]
]) test(`reject ${name}`, async () => {
  const input = command(); change(input.payload); assert.equal((await invoke(input)).responseCode, 400); assert.equal(callbacks.length, 0);
});
test('reject signed duplicate and unknown envelope properties', async () => {
  const input = command();
  for (const raw of [Buffer.from(JSON.stringify(input).replace('"previousCursor":null', '"previousCursor":null,"previousCursor":null')),
    Buffer.from(JSON.stringify({ ...input, extra: true }))]) {
    assert.equal((await invoke(input, { raw })).responseCode, 400); assert.equal(callbacks.length, 0);
  }
});
test('requires two explicitly configured distinct accounts', () => {
  assert.throws(() => profileFor('', secondary)); assert.throws(() => profileFor(primary, primary));
});
test('callback rejection prevents acknowledgement', async () => {
  status = 409; await assert.rejects(invoke(command()), /callback-not-accepted/);
});
test('invalid callback TLS prevents acknowledgement', async () => {
  tls = false; await assert.rejects(invoke(command()), /callback-not-accepted/);
});
