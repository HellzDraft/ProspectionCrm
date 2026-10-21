'use strict';
const { test, after, beforeEach } = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('node:crypto'), https = require('node:https');
const { EventEmitter } = require('node:events');
const { CrmCalendarSync, profileFor } = require('./CrmCalendarSync.node');
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
  return { version: 1, messageId: crypto.randomUUID(), operationId: crypto.randomUUID(), correlationId: crypto.randomUUID(), type: 'calendar.sync',
    payload: { calendarRef: account, jobRef: crypto.randomUUID(), previousCursor: cursor, maxItems } };
}
async function invoke(value, { raw = Buffer.from(JSON.stringify(value)), bad = false, expired = false, changed = false } = {}) {
  const time = String(Math.floor(Date.now() / 1000) - (expired ? 301 : 0));
  const headers = { 'content-type': 'application/json', 'x-crm-key-id': credentials.incomingId, 'x-crm-timestamp': time,
    'x-crm-signature': bad ? '0'.repeat(64) : sign(incoming, 'crm-to-n8n', credentials.incomingId, time, '/webhook/crm-calendar-sync-demo', raw) };
  const context = { getInputData: () => [{ json: { headers }, binary: { data: {} } }], getCredentials: async () => credentials,
    getNodeParameter: name => name === 'primaryCalendarRef' ? primary : secondary,
    helpers: { getBinaryDataBuffer: async () => changed ? Buffer.concat([raw, Buffer.from(' ')]) : raw } };
  return (await new CrmCalendarSync().execute.call(context))[0][0].json;
}

test('A/B/C/D/E fixtures: revisions, cancellations, identities and exclusive DST end', async () => {
  await invoke(command()); const a = callbacks[0].payload;
  assert.equal(a.events.length, 3); assert.equal(a.nextCursor, 'cursor-001');
  assert.equal((Date.parse(a.events[1].endsAt)-Date.parse(a.events[1].startsAt))/3600000,23);
  await invoke(command('cursor-001')); const b=callbacks[1].payload;
  assert.deepEqual(b.events[0],a.events[0]); assert.equal(b.events[1].revision,2); assert.equal(b.events[2].status,'cancelled');
  assert.equal(b.events[2].occurrenceId,a.events[2].occurrenceId);
  await invoke(command('cursor-002')); assert.deepEqual(callbacks[2].payload.events,[]); assert.equal(callbacks[2].payload.nextCursor,'cursor-003');
  await invoke(command('cursor-003')); assert.equal(callbacks[3].payload.events[1].revision,1); assert.notEqual(callbacks[3].payload.events[1].title,a.events[0].title);
  await invoke(command(null,secondary)); assert.equal(callbacks[4].payload.events[0].externalEventId,a.events[0].externalEventId); assert.notEqual(callbacks[4].payload.events[0].title,a.events[0].title);
});
for (const mode of ['bad','expired','changed']) test(`reject ${mode} signature`,async()=>{
 assert.equal((await invoke(command(),{[mode]:true})).responseCode,401);assert.equal(callbacks.length,0);
});
for (const [name,change] of [
 ['extra',p=>p.extra=true],['missing job',p=>delete p.jobRef],['bad job',p=>p.jobRef='bad'],['other calendar',p=>p.calendarRef=crypto.randomUUID()],
 ['cursor type',p=>p.previousCursor=2],['long cursor',p=>p.previousCursor='x'.repeat(2049)],['missing cursor',p=>delete p.previousCursor],
 ['max zero',p=>p.maxItems=0],['max huge',p=>p.maxItems=101],['fraction',p=>p.maxItems=1.5],['max insufficient',p=>p.maxItems=2]
]) test(`reject ${name}`,async()=>{const c=command();change(c.payload);assert.equal((await invoke(c)).responseCode,400);assert.equal(callbacks.length,0);});
for (const [name,raw] of [
 ['duplicate',()=>Buffer.from(JSON.stringify(command()).replace('"previousCursor":null','"previousCursor":null,"previousCursor":null'))],
 ['malformed',()=>Buffer.from('{')],['invalid UTF8',()=>Buffer.from([0xff])],['envelope extra',()=>Buffer.from(JSON.stringify({...command(),extra:true}))]
]) test(`reject parsing ${name}`,async()=>{assert.equal((await invoke(command(),{raw:raw()})).responseCode,400);assert.equal(callbacks.length,0);});
test('calendar command boundary 16 KiB inclusive',async()=>{
 const c=command(),json=JSON.stringify(c);assert.equal((await invoke(c,{raw:Buffer.from(json+' '.repeat(16384-Buffer.byteLength(json)))})).responseCode,202);
 assert.equal((await invoke(c,{raw:Buffer.from(json+' '.repeat(16385-Buffer.byteLength(json)))})).responseCode,413);
});
test('callback limit 512 KiB inclusive; not globally raised',async()=>{
 const {sendCallback}=require('../shared/CrmLocalHmac');
 const result={type:'calendar.sync',padding:''};const size=Buffer.byteLength(JSON.stringify(result));result.padding='x'.repeat(524288-size);
 await sendCallback(credentials,result);result.padding+='x';await assert.rejects(sendCallback(credentials,result),/callback-too-large/);
 await assert.rejects(sendCallback(credentials,{type:'email.send',padding:'x'.repeat(4096)}),/callback-too-large/);
});
test('callback rejection prevents ACK',async()=>{status=409;await assert.rejects(invoke(command()),/callback-not-accepted/);});
test('invalid TLS prevents ACK',async()=>{tls=false;await assert.rejects(invoke(command()),/callback-not-accepted/);});

for (const [name, change] of [
 ['version',c=>c.version=2],['type',c=>c.type='email.sync'],['null payload',c=>c.payload=null],
 ['nested cursor',c=>c.payload.previousCursor={}],['null envelope',()=>null]
]) test(`closed command ${name}`,async()=>{let c=command();const value=change(c);if(name==='null envelope')c=value;assert.equal((await invoke(c)).responseCode,400);assert.equal(callbacks.length,0);});
test('escaped duplicate properties rejected',async()=>{
 const raw=Buffer.from(JSON.stringify(command()).replace('"previousCursor":null','"previousCursor":null,"previousCu\\u0072sor":null'));
 assert.equal((await invoke(command(),{raw})).responseCode,400);assert.equal(callbacks.length,0);
});
