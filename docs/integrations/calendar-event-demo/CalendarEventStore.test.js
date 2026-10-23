'use strict';
const {test,afterEach}=require('node:test'), assert=require('node:assert/strict');
const fs=require('node:fs'),os=require('node:os'),path=require('node:path'),crypto=require('node:crypto');
const {spawn,spawnSync}=require('node:child_process');
const {CalendarEventStore}=require('./CalendarEventStore');
const {hash,snapshotText,eventHash,eventFromTarget}=require('./CalendarEventContract');
const scope={workspaceId:crypto.randomUUID(),connectionId:crypto.randomUUID(),calendarAccountId:crypto.randomUUID(),calendarId:crypto.randomUUID(),providerCode:'local-demo',externalCalendarId:'synthetic-calendar'};
const roots=[];
afterEach(()=>{for(const dir of roots.splice(0))fs.rmSync(dir,{recursive:true,force:true});});
function create(target=null){
 const deliveryId=crypto.randomUUID();
 const snapshot={version:1,planId:crypto.randomUUID(),workspaceId:scope.workspaceId,calendarAccountId:scope.calendarAccountId,connectionId:scope.connectionId,revision:0,
  content:{calendarId:scope.calendarId,operationCode:target?'update':'create',targetCalendarEventId:target?.id??null,title:'Synthetic é <meeting> + "quoted"',description:'',location:null,
   startsAt:'2026-10-24T22:00:00.0000000+00:00',endsAt:'2026-10-25T23:00:00.0000000+00:00',isAllDay:true,timeZoneId:'Europe/Paris',opportunityId:null,companyId:null,contactId:null},target};
 return {version:1,messageId:crypto.randomUUID(),operationId:crypto.randomUUID(),correlationId:crypto.randomUUID(),type:'calendar.event.'+snapshot.content.operationCode,
  deliveryId,idempotencyKey:'calendar-event-'+deliveryId,contentHash:hash(snapshotText(snapshot)),providerCode:'local-demo',externalCalendarId:scope.externalCalendarId,snapshot,
  precondition:target?{calendarId:scope.calendarId,externalCalendarId:scope.externalCalendarId,externalEventId:target.externalEventId,expectedRevision:target.revision,targetContentHash:target.contentHash}:null,notifications:'disabled'};
}
const fixture={externalEventId:'independent-fixture',revision:7,title:'Before',description:null,location:null,startsAt:'2026-10-25T01:30:00+00:00',endsAt:'2026-10-25T02:30:00+00:00',isAllDay:false,timeZoneId:'Europe/Paris',status:'confirmed',seriesId:'series',occurrenceId:'occurrence'};
function target(e=fixture){return {id:crypto.randomUUID(),providerCode:'local-demo',externalEventId:e.externalEventId,revision:e.revision,contentHash:eventHash(e),title:e.title,description:e.description,
 location:e.location,startsAt:e.startsAt,endsAt:e.endsAt,isAllDay:e.isAllDay,timeZoneId:e.timeZoneId,statusCode:e.status,seriesId:e.seriesId,occurrenceId:e.occurrenceId,opportunityId:null,contactId:null};}
function database(fixtures=[]){const dir=fs.mkdtempSync(path.join(os.tmpdir(),'crm-calendar-registry-'));roots.push(dir);const file=path.join(dir,'provider.sqlite');CalendarEventStore.initialize(file,[scope],fixtures.map(event=>({calendarId:scope.calendarId,event})));return file;}
function child(file,c,crash){
 const command=path.join(path.dirname(file),crypto.randomUUID()+'.json');fs.writeFileSync(command,JSON.stringify(c));
 const code=`const {CalendarEventStore}=require(process.argv[1]);const fs=require('node:fs');const s=new CalendarEventStore(process.argv[2]);const r=s.execute(JSON.parse(fs.readFileSync(process.argv[3])),stage=>{if(stage===process.argv[4])process.exit(71);});if(process.argv[4]==='after-commit')process.exit(72);s.close();console.log(JSON.stringify(r));`;
 return {command,args:['-e',code,path.join(__dirname,'CalendarEventStore.js'),file,command,crash??'']};
}
test('atomic create, exact idempotence, durable lookup after close and new process',()=>{
 const file=database(),c=create();let s=new CalendarEventStore(file);const a=s.execute(c);assert.equal(a.proof.outcome,'applied');assert.match(a.proof.event.externalEventId,/^simulated-calendar-/);
 assert.deepEqual(s.execute(c),a);assert.deepEqual(s.counts(),{ledger:1,effects:1,events:1});s.close();
 const run=spawnSync(process.execPath,child(file,c).args,{encoding:'utf8'});assert.equal(run.status,0,run.stderr);assert.deepEqual(JSON.parse(run.stdout),a);
 s=new CalendarEventStore(file);assert.deepEqual(s.lookup(c.idempotencyKey),a.proof);assert.equal(s.counts().effects,1);s.close();
});
test('same key with changed command fails without extra effect',()=>{
 const s=new CalendarEventStore(database()),c=create();s.execute(c);assert.throws(()=>s.execute({...c,messageId:crypto.randomUUID()}),/key-conflict/);assert.equal(s.counts().effects,1);s.close();
});
test('update compares an independently seeded occurrence and retains identity',()=>{
 const s=new CalendarEventStore(database([fixture])),c=create(target());const r=s.execute(c).proof;
 assert.equal(r.outcome,'applied');assert.equal(r.event.revision,8);assert.equal(r.event.externalEventId,fixture.externalEventId);assert.equal(r.event.seriesId,fixture.seriesId);assert.equal(r.event.occurrenceId,fixture.occurrenceId);assert.equal(s.counts().effects,1);s.close();
});
test('missing provider target is never seeded by update command',()=>{
 const s=new CalendarEventStore(database()),r=s.execute(create(target())).proof;assert.equal(r.outcome,'not-applied');assert.equal(r.diagnosticCode,'TargetMissing');assert.equal(s.counts().events,0);s.close();
});
test('provider revision moves independently after approval; obsolete command cannot overwrite',()=>{
 const s=new CalendarEventStore(database([fixture])),c=create(target());s.advance(scope.calendarId,fixture.externalEventId,7,{...fixture,revision:8,title:'Concurrent'});
 const r=s.execute(c).proof;assert.equal(r.outcome,'version-conflict');assert.equal(r.observedRevision,8);assert.equal(s.event(scope.calendarId,fixture.externalEventId).title,'Concurrent');assert.equal(s.counts().effects,0);s.close();
});
for(const change of ['content','status','occurrence'])test('same revision altered '+change+' is certainly not applied',()=>{
 const changed={...fixture,...(change==='content'?{title:'Changed'}:change==='status'?{status:'cancelled'}:{occurrenceId:'other'})};
 const s=new CalendarEventStore(database([changed]));assert.equal(s.execute(create(target())).proof.outcome,'not-applied');assert.equal(s.counts().effects,0);s.close();
});
for(const mode of ['not-applied','unknown','withhold-callback'])test('durable explicit '+mode+' differs from HTTP acknowledgement',()=>{
 const s=new CalendarEventStore(database()),c=create();s.policy(c.snapshot.planId,mode);const r=s.execute(c);assert.equal(r.withhold,mode==='withhold-callback');
 assert.equal(r.proof.outcome,mode==='withhold-callback'?'applied':mode);assert.equal(r.proof.provenAt===null,mode==='unknown');assert.equal(s.counts().effects,mode==='withhold-callback'?1:0);assert.deepEqual(s.lookup(c.idempotencyKey),r.proof);s.close();
});
for(const crash of ['after-event-before-ledger','before-commit','after-commit'])test('process crash '+crash+' recovers atomically',()=>{
 const file=database(),c=create(),run=spawnSync(process.execPath,child(file,c,crash).args,{encoding:'utf8'});assert.equal(run.status,crash==='after-commit'?72:71,run.stderr);
 const s=new CalendarEventStore(file),committed=crash==='after-commit';assert.deepEqual(s.counts(),{ledger:committed?1:0,effects:committed?1:0,events:committed?1:0});
 assert.equal(s.lookup(c.idempotencyKey)!==null,committed);if(committed)assert.equal(s.lookup(c.idempotencyKey).outcome,'applied');s.close();
});
test('independent concurrent processes serialize one idempotent effect',async()=>{
 const file=database(),c=create();
 const run=()=>new Promise((resolve,reject)=>{const p=spawn(process.execPath,child(file,c).args);let err='';p.stderr.on('data',x=>err+=x);p.on('error',reject);p.on('close',code=>code===0?resolve():reject(Error(err)));});
 await Promise.all(Array.from({length:6},run));const s=new CalendarEventStore(file);assert.deepEqual(s.counts(),{ledger:1,effects:1,events:1});s.close();
});
test('two independently approved updates compete atomically for the provider revision',async()=>{
 const file=database([fixture]),a=create(target()),b=create(target());
 const run=c=>new Promise((resolve,reject)=>{const p=spawn(process.execPath,child(file,c).args);let out='',err='';p.stdout.on('data',x=>out+=x);p.stderr.on('data',x=>err+=x);p.on('close',code=>code===0?resolve(JSON.parse(out).proof):reject(Error(err)));});
 const proofs=await Promise.all([run(a),run(b)]);assert.deepEqual(proofs.map(x=>x.outcome).sort(),['applied','version-conflict']);const s=new CalendarEventStore(file);assert.equal(s.counts().effects,1);s.close();
});
test('missing, truncated and corrupt registries fail closed without reset',()=>{
 const file=database();assert.throws(()=>CalendarEventStore.initialize(file,[scope]),/EEXIST/);fs.writeFileSync(file,'partial');assert.throws(()=>new CalendarEventStore(file));assert.equal(fs.readFileSync(file,'utf8'),'partial');assert.throws(()=>new CalendarEventStore(file+'-missing'),/missing/);
});
module.exports={scope,create,fixture,target};
