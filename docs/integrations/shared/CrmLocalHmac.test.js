'use strict';
// Offline protocol checks only: synthetic keys and mocked HTTPS, no CRM/n8n call.
const { test, after, beforeEach } = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto');
const https = require('node:https');
const { EventEmitter } = require('node:events');
const { CrmProbe } = require('../technical-probe-local/CrmProbe.node');
const { CrmCollection } = require('../remote-collection-demo/CrmCollection.node');
const incoming = crypto.randomBytes(32), outgoing = crypto.randomBytes(32);
const credentials = { incomingId: 'synthetic-in', incomingSecret: incoming.toString('base64'),
  outgoingId: 'synthetic-out', outgoingSecret: outgoing.toString('base64'), caPem: 'synthetic-public-ca' };
const signature = (key, audience, id, time, target, body) => crypto.createHmac('sha256', key)
  .update(`crm-hmac-v1\n${audience}\n${id}\n${time}\nPOST\n${target}\n${crypto.createHash('sha256').update(body).digest('hex')}`).digest('hex');
let sent, status, tls;
beforeEach(() => { sent = []; status = 202; tls = true; });
const originalRequest = https.request;
https.request = (options, callback) => {
  assert.equal(options.hostname, 'localhost'); assert.equal(options.port, 7096);
  assert.equal(options.path, '/api/integrations/results'); assert.equal(options.rejectUnauthorized, true);
  assert.equal(options.ca, credentials.caPem); assert.equal(options.minVersion, 'TLSv1.2');
  const request = new EventEmitter();
  request.end = body => {
    assert.equal(options.headers['Content-Length'], body.length);
    assert.equal(options.headers['X-CRM-Signature'], signature(outgoing, 'n8n-to-crm', credentials.outgoingId,
      options.headers['X-CRM-Timestamp'], options.path, body));
    sent.push(JSON.parse(body));
    setImmediate(() => {
      const response = new EventEmitter(); response.statusCode = status; response.socket = { authorized: tls };
      callback(response);
      response.emit('data', Buffer.from(JSON.stringify({status: 'received', alreadyRecorded: false})));
      response.emit('end');
    });
  };
  return request;
};
after(() => { https.request = originalRequest; });
function command(type) {
  return { version: 1, messageId: crypto.randomUUID(), operationId: crypto.randomUUID(), correlationId: crypto.randomUUID(), type,
    payload: type === 'technical.probe' ? { probeId: crypto.randomUUID() } : { requestId: crypto.randomUUID(), query: 'phase-9-4-3-demo', maxItems: 2 } };
}
async function invoke(Node, value, settings = {}) {
  const target = Node === CrmProbe ? '/webhook/crm-technical-probe' : '/webhook/crm-remote-collection-demo';
  const raw = settings.raw ?? Buffer.from(JSON.stringify(value, null, 2));
  const timestamp = String(Math.floor(Date.now()/1000) + (settings.expired ? -301 : 0));
  const headers = { 'content-type': 'application/json; charset=utf-8', 'x-crm-key-id': credentials.incomingId,
    'x-crm-timestamp': timestamp, 'x-crm-signature': signature(incoming, 'crm-to-n8n', credentials.incomingId, timestamp, target, raw) };
  if(settings.bad) headers['x-crm-signature'] = '0'.repeat(64);
  const context = { getInputData: () => [{ json: { headers }, binary: { data: {} } }],
    getCredentials: async name => { assert.equal(name, 'crmProbe'); return credentials; },
    helpers: { getBinaryDataBuffer: async (index, name) => { assert.equal(index, 0); assert.equal(name, 'data'); return settings.changedRaw ?? raw; } } };
  return (await new Node().execute.call(context))[0][0].json;
}
for (const [type, Node] of [['technical.probe', CrmProbe], ['collection.request', CrmCollection]]) {
  test(`${type}: exact raw HMAC, HTTPS callback, 202 only after callback`, async () => {
    const input = command(type); const output = await invoke(Node, input);
    assert.equal(output.responseCode, 202); assert.equal(output.callbackTlsValidated, true); assert.equal(sent.length, 1);
    const result = sent[0]; assert.equal(result.type, type); assert.equal(result.commandMessageId, input.messageId);
    assert.equal(result.operationId, input.operationId); assert.equal(result.correlationId, input.correlationId);
    assert.notEqual(result.messageId, input.messageId); assert.equal(result.payload.outcome, 'succeeded');
    if(Node === CrmProbe) assert.equal(result.payload.probeId, input.payload.probeId);
    else {
      assert.equal(result.payload.requestId, input.payload.requestId);
      assert.deepEqual(result.payload.items.map(x => x.externalId), ['remote-demo-001','remote-demo-002']);
      assert.deepEqual(result.payload.items.map(x => x.title), ['Remote Demo .NET Backend','Remote Demo Unity Tools']);
      assert.ok(result.payload.items.every(x => x.location === 'Bordeaux' && !('sourceUrl' in x) && !('companyName' in x)));
    }
    assert.ok(!JSON.stringify(output).includes(credentials.incomingSecret)); assert.ok(!JSON.stringify(output).includes(credentials.outgoingSecret));
  });
  for (const mode of ['bad', 'expired', 'changedRaw']) test(`${type}: rejects ${mode} without callback`, async () => {
    const input = command(type), settings = { [mode]: mode === 'changedRaw' ? Buffer.from(JSON.stringify(input)) : true };
    assert.equal((await invoke(Node, input, settings)).responseCode, 401); assert.equal(sent.length, 0);
  });
  test(`${type}: rejects signed duplicate properties`, async () => {
    const input = command(type), raw = Buffer.from(JSON.stringify(input).replace('"version":1', '"version":1,"version":1'));
    assert.equal((await invoke(Node, input, {raw})).responseCode, 400); assert.equal(sent.length, 0);
  });
}
for (const [field,value] of [['version',2],['type','technical.probe'],['requestId','invalid'],['query','other'],['maxItems',1],['maxItems',101],['maxItems',2.5]]) {
  test(`collection: rejects invalid ${field} ${value}`, async () => {
    const input = command('collection.request'); (field in input ? input : input.payload)[field] = value;
    assert.equal((await invoke(CrmCollection,input)).responseCode,400); assert.equal(sent.length,0);
  });
}
test('collection: callback rejection prevents ACK', async () => {
  status = 409; await assert.rejects(invoke(CrmCollection, command('collection.request')), /callback-not-accepted/);
});
test('collection: unauthenticated TLS prevents ACK', async () => {
  tls = false; await assert.rejects(invoke(CrmCollection, command('collection.request')), /callback-not-accepted/);
});
