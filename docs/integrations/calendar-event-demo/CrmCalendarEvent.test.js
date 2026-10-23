'use strict';
const {test,beforeEach,afterEach,after}=require('node:test'),assert=require('node:assert/strict');
const crypto=require('node:crypto'),https=require('node:https'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
const {EventEmitter}=require('node:events');
const {executeSignedCommand,sendCallback}=require('../shared/CrmLocalHmac');
const {profileFor}=require('./CrmCalendarEvent.node');
const {CalendarEventStore}=require('./CalendarEventStore');
const {snapshotText,eventHash,validCommand,hash}=require('./CalendarEventContract');
const golden=require('./contract-golden.json'),scope=golden.scope;
const incoming=crypto.randomBytes(32),outgoing=crypto.randomBytes(32);
const credentials={incomingId:'synthetic-in',incomingSecret:incoming.toString('base64'),outgoingId:'synthetic-out',outgoingSecret:outgoing.toString('base64'),caPem:'synthetic-ca'};
const sign=(key,audience,id,time,target,body)=>crypto.createHmac('sha256',key).update(['crm-hmac-v1',audience,id,time,'POST',target,crypto.createHash('sha256').update(body).digest('hex')].join('\n')).digest('hex');
let dir,file,callbacks,status,tls;
beforeEach(()=>{dir=fs.mkdtempSync(path.join(os.tmpdir(),'crm-calendar-protocol-'));file=path.join(dir,'provider.sqlite');CalendarEventStore.initialize(file,[scope],[{calendarId:scope.calendarId,event:golden.providerEvent}]);callbacks=[];status=202;tls=true;});
afterEach(()=>fs.rmSync(dir,{recursive:true,force:true}));
const original=https.request;
https.request=(options,callback)=>{
 assert.equal(options.hostname,'localhost');assert.equal(options.port,7096);assert.equal(options.path,'/api/integrations/results');assert.equal(options.rejectUnauthorized,true);assert.equal(options.ca,credentials.caPem);
 const req=new EventEmitter();req.end=body=>{
  assert.equal(body.length,options.headers['Content-Length']);assert.equal(options.headers['X-CRM-Signature'],sign(outgoing,'n8n-to-crm',credentials.outgoingId,options.headers['X-CRM-Timestamp'],options.path,body));
  callbacks.push(JSON.parse(body));setImmediate(()=>{const r=new EventEmitter();r.socket={authorized:tls};r.statusCode=status;callback(r);r.emit('data',Buffer.from(JSON.stringify({status:'received',alreadyRecorded:false})));r.emit('end');});
 };return req;
};
after(()=>{https.request=original;});
function command(update=false){return structuredClone(update?golden.update:golden.create);}
async function invoke(c,options={}){
 const raw=options.raw??Buffer.from(JSON.stringify(c)),time=String(Math.floor(Date.now()/1000)-(options.expired?301:0));
 const headers={'content-type':'application/json','x-crm-key-id':credentials.incomingId,'x-crm-timestamp':time,
  'x-crm-signature':options.bad?'0'.repeat(64):sign(incoming,options.audience??'crm-to-n8n',credentials.incomingId,time,options.target??'/webhook/crm-calendar-event-demo',raw)};
 Object.assign(headers,options.headers);
 const context={getInputData:()=>[{json:{headers,query:options.query??{}},binary:{data:{}}}],getCredentials:async()=>credentials,helpers:{getBinaryDataBuffer:async()=>options.changed?Buffer.concat([raw,Buffer.from(' ')]):raw}};
 return (await executeSignedCommand.call(context,profileFor(scope,file)))[0][0].json;
}
test('C# golden UTF-8 snapshots, escaping, microseconds and provider hashes match byte for byte',()=>{
 assert.equal(snapshotText(golden.create.snapshot),golden.createCanonical);assert.equal(snapshotText(golden.update.snapshot),golden.updateCanonical);
 assert.equal(eventHash(golden.providerEvent),golden.eventHash);assert.equal(validCommand(golden.create,scope),true);assert.equal(validCommand(golden.update,scope),true);
});
for(const update of [false,true])test('actual shared HMAC wrapper accepts '+(update?'UPDATE':'CREATE')+' and signs a TLS-validated proof',async()=>{
 const c=command(update),r=await invoke(c);assert.equal(r.responseCode,202);assert.equal(r.signatureValidated,true);assert.equal(r.callbackTlsValidated,true);assert.equal(callbacks.length,1);
 const p=callbacks[0];assert.equal(p.outcome,'applied');assert.equal(p.contentHash,c.contentHash);assert.equal(p.commandMessageId,c.messageId);assert.equal(p.calendarId,scope.calendarId);
 assert.equal(JSON.stringify(r).includes(credentials.incomingSecret),false);assert.equal(JSON.stringify(r).includes(credentials.outgoingSecret),false);
 await invoke(c);assert.deepEqual(callbacks[1],p);const s=new CalendarEventStore(file);assert.equal(s.counts().effects,1);s.close();
});
for(const [name,opt]of [['bad',{bad:true}],['expired',{expired:true}],['altered bytes',{changed:true}],['audience',{audience:'n8n-to-crm'}],['target',{target:'/other'}],['key',{headers:{'x-crm-key-id':'other'}}]])test('reject signature '+name,async()=>{
 assert.equal((await invoke(command(),opt)).responseCode,401);assert.equal(callbacks.length,0);const s=new CalendarEventStore(file);assert.equal(s.counts().ledger,0);s.close();
});
for(const [name,change]of [
 ['unknown envelope',c=>c.attendees=[]],['unknown content',c=>c.snapshot.content.sendUpdates='all'],['unknown snapshot',c=>c.snapshot.oauthToken='forbidden'],
 ['notifications',c=>c.notifications='enabled'],['type',c=>c.type='calendar.event.delete'],['version',c=>c.version=2],['missing',c=>delete c.contentHash],
 ['id',c=>c.operationId=''],['idempotency',c=>c.idempotencyKey='other'],['hash',c=>c.contentHash='0'.repeat(64)],['calendar',c=>c.externalCalendarId='other'],
 ['provider',c=>c.providerCode='google'],['connection',c=>c.snapshot.connectionId=crypto.randomUUID()],['workspace',c=>c.snapshot.workspaceId=crypto.randomUUID()],
 ['content',c=>c.snapshot.content.title='Unapproved'],['precondition',c=>c.precondition={}],['boolean',c=>c.snapshot.content.isAllDay='true']
])test('closed command rejects '+name,async()=>{const c=command();change(c);assert.equal((await invoke(c)).responseCode,400);assert.equal(callbacks.length,0);});
for(const field of ['calendarId','externalCalendarId','externalEventId','expectedRevision','targetContentHash'])test('update precondition rejects '+field,async()=>{
 const c=command(true);c.precondition[field]=field==='expectedRevision'?9:'different';assert.equal((await invoke(c)).responseCode,400);assert.equal(callbacks.length,0);
});
for(const [name,raw]of [
 ['duplicate',()=>JSON.stringify(command()).replace('"version":1','"version":1,"version":1')],
 ['nested escaped duplicate',()=>JSON.stringify(command()).replace('"notifications":"disabled"','"notifications":"disabled","notificatio\\u006es":"disabled"')],
 ['invalid JSON',()=>'{'],['invalid UTF8',()=>Buffer.from([0xff])],['null',()=> 'null'],['deep',()=>'{"a":{"a":{"a":{"a":{"a":{"a":{"a":0}}}}}}}']
])test('reject parsing '+name,async()=>{assert.equal((await invoke(command(),{raw:Buffer.from(raw())})).responseCode,400);assert.equal(callbacks.length,0);});
test('command 65536-byte inclusive boundary and oversized rejection',async()=>{
 const c=command(),json=JSON.stringify(c);assert.equal((await invoke(c,{raw:Buffer.from(json+' '.repeat(65536-Buffer.byteLength(json)))})).responseCode,202);
 assert.equal((await invoke(c,{raw:Buffer.from(json+' '.repeat(65537-Buffer.byteLength(json)))})).responseCode,413);assert.equal(callbacks.length,1);
});
test('result 65536-byte inclusive boundary; old protocol limits unchanged',async()=>{
 for(const type of ['calendar.event.create','calendar.event.update']){const r={type,padding:''},n=Buffer.byteLength(JSON.stringify(r));r.padding='x'.repeat(65536-n);await sendCallback(credentials,r);r.padding+='x';await assert.rejects(sendCallback(credentials,r),/callback-too-large/);}
 await assert.rejects(sendCallback(credentials,{type:'email.send',padding:'x'.repeat(4096)}),/callback-too-large/);
 await assert.rejects(sendCallback(credentials,{type:'calendar.sync',padding:'x'.repeat(524288)}),/callback-too-large/);
});
test('lost callback returns ambiguous 503 but retains proof and single effect',async()=>{
 const c=command(),s=new CalendarEventStore(file);s.policy(c.snapshot.planId,'withhold-callback');const r=await invoke(c);assert.equal(r.responseCode,503);assert.equal(callbacks.length,0);assert.equal(s.lookup(c.idempotencyKey).outcome,'applied');assert.equal(s.counts().effects,1);s.close();
});
for(const mode of ['rejection','tls'])test('callback '+mode+' cannot produce ACK',async()=>{
 if(mode==='rejection')status=409;else tls=false;await assert.rejects(invoke(command()),/callback-not-accepted/);const s=new CalendarEventStore(file);assert.equal(s.counts().effects,1);assert.ok(s.lookup(command().idempotencyKey));s.close();
});
for(const [start,end,hours]of [['2026-03-28T23:00:00.0000000+00:00','2026-03-29T22:00:00.0000000+00:00',23],['2026-10-24T22:00:00.0000000+00:00','2026-10-25T23:00:00.0000000+00:00',25]])test('Europe/Paris exclusive all-day '+hours+' hours',async()=>{
 const c=command();c.snapshot.content.startsAt=start;c.snapshot.content.endsAt=end;c.contentHash=hash(snapshotText(c.snapshot));assert.equal((await invoke(c)).responseCode,202);assert.equal((Date.parse(callbacks[0].event.endsAt)-Date.parse(callbacks[0].event.startsAt))/3600000,hours);
});
test('invalid IANA, non-midnight all-day, absent offset and reversed dates are rejected even with recomputed hash',async()=>{
 for(const change of [p=>p.timeZoneId='Invalid/Zone',p=>p.startsAt='2026-10-24T21:00:00.0000000+00:00',p=>p.startsAt='2026-10-24T22:00:00',p=>p.endsAt=p.startsAt]){
  const c=command();change(c.snapshot.content);c.contentHash=hash(snapshotText(c.snapshot));assert.equal((await invoke(c)).responseCode,400);
 }
 assert.equal(callbacks.length,0);
});
