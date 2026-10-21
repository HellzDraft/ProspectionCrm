'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const crypto = require('crypto'), fs = require('fs'), os = require('os'), path = require('path');
const { profileFor } = require('./CrmEmailSend.node');
const { uniqueProperties, executeSignedCommand } = require('../shared/CrmLocalHmac');
function fixture(subject = 'phase9542-accepted') {
  const dir = fs.mkdtempSync(path.join(os.tmpdir(),'crm-email-simulation-'));
  const a=crypto.randomUUID(), w=crypto.randomUUID(), n=crypto.randomUUID(), d=crypto.randomUUID();
  const command = { version:1, messageId:crypto.randomUUID(),operationId:crypto.randomUUID(),correlationId:crypto.randomUUID(),type:'email.send',deliveryId:d,idempotencyKey:`email-send-${d}`,
    snapshot:{version:1,planId:crypto.randomUUID(),workspaceId:w,emailAccountId:a,connectionId:n,revision:0,fromAddress:'sender@example.invalid',toAddresses:['recipient@example.invalid'],ccAddresses:[],subject,bodyText:'a'.repeat(5000),opportunityId:null,companyId:null,contactId:null} };
  return {dir, command, profile:profileFor(a,w,n,dir), reopen:()=>profileFor(a,w,n,dir)};
}
test('durable one acceptance and identical proof across restart; conflicting command refused',()=>{
  const f=fixture(); try {
    const first=f.profile.result(f.command); const second=f.reopen().result(f.command);
    assert.deepEqual(first,second); assert.equal(first.providerCode,'local-demo');
    assert.equal(JSON.parse(fs.readFileSync(path.join(f.dir,`${f.command.idempotencyKey}.json`))).acceptances,1);
    f.command.snapshot.bodyText='different'; assert.throws(()=>f.profile.result(f.command),/key-conflict/);
  } finally { fs.rmSync(f.dir,{recursive:true}); }
});
for(const [mode,outcome,count] of [['late','accepted',1],['not-sent','not-sent',0],['failed','failed',0]]) test(`persistent ${mode} evidence`,()=>{
  const f=fixture(`phase9542-${mode}`); try {
    const result=f.profile.result(f.command); if(mode==='late')assert.equal(result,null);
    const state=JSON.parse(fs.readFileSync(path.join(f.dir,`${f.command.idempotencyKey}.json`)));
    assert.equal(state.proof.outcome,outcome); assert.equal(state.acceptances,count);
    assert.deepEqual(f.reopen().result(f.command),result);
  } finally {fs.rmSync(f.dir,{recursive:true});}
});
test('strict snapshot scope, addresses and fields',()=>{
  const f=fixture(); try {
    assert.equal(f.profile.validCommand(f.command),true);
    for(const change of [c=>c.snapshot.workspaceId=crypto.randomUUID(),c=>c.snapshot.toAddresses=['real@example.com'],c=>c.snapshot.extra=1,c=>c.extra=1,c=>c.idempotencyKey='other',c=>c.snapshot.bodyText='a'.repeat(32001)]){
      const c=structuredClone(f.command); change(c); assert.equal(f.profile.validCommand(c),false);
    }
    assert.equal(uniqueProperties('{"version":1,"snapshot":{"version":1}}'),true);
    assert.equal(uniqueProperties('{"snapshot":{"version":1,"ver\\u0073ion":1}}'),false);
    assert.equal(uniqueProperties('{"bodyText":"fake \\"name\\": value"}'),true);
  } finally{fs.rmSync(f.dir,{recursive:true});}
});
test('raw HMAC accepts >4096 byte email.send; rejects tampering, duplicates, excess size before simulation',async()=>{
  const f=fixture('phase9542-late'); const incoming=crypto.randomBytes(32), outgoing=crypto.randomBytes(32);
  async function invoke(raw, bad=false) {
    const timestamp=String(Math.floor(Date.now()/1000)), id='test-in';
    const sig=crypto.createHmac('sha256',incoming).update(['crm-hmac-v1','crm-to-n8n',id,timestamp,'POST',f.profile.target,crypto.createHash('sha256').update(raw).digest('hex')].join('\n')).digest('hex');
    return (await executeSignedCommand.call({getInputData:()=>[{json:{headers:{'content-type':'application/json','x-crm-key-id':id,'x-crm-timestamp':timestamp,'x-crm-signature':bad?'0'.repeat(64):sig}},binary:{data:{}}}],
      getCredentials:async()=>({incomingId:id,incomingSecret:incoming.toString('base64'),outgoingSecret:outgoing.toString('base64')}),helpers:{getBinaryDataBuffer:async()=>raw}},f.profile))[0][0].json;
  }
  try {
    const raw=Buffer.from(JSON.stringify(f.command)); assert.ok(raw.length>4096);
    assert.equal((await invoke(raw,true)).responseCode,401);
    assert.equal((await invoke(Buffer.from(raw.toString().replace('"version":1','"version":1,"version":1')))).responseCode,400);
    assert.equal((await invoke(Buffer.alloc(65537,32))).responseCode,413);
    assert.equal(fs.readdirSync(f.dir).length,0);
    assert.equal((await invoke(raw)).responseCode,503); assert.equal(fs.readdirSync(f.dir).length,1);
  } finally {fs.rmSync(f.dir,{recursive:true});}
});
