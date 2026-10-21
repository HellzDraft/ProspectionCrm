'use strict';
const crypto = require('crypto');
const fs = require('fs');
const path = require('path');
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');
const shape = (o, fields) => o && !Array.isArray(o) && typeof o === 'object' && Object.keys(o).sort().join(',') === fields.sort().join(',');
const address = s => typeof s === 'string' && /^[a-z0-9.!#$%&'*+/=?^_`{|}~-]+@example\.invalid$/.test(s);
const snapshotFields = ['version','planId','workspaceId','emailAccountId','connectionId','revision','fromAddress','toAddresses','ccAddresses','subject','bodyText','opportunityId','companyId','contactId'];
function profileFor(account, workspace, connection, directory) {
  if (![account, workspace, connection].every(isId) || !path.isAbsolute(directory)) throw Error('configure-local-simulator');
  function validCommand(c) {
    if (!shape(c, ['version','messageId','operationId','correlationId','type','deliveryId','idempotencyKey','snapshot'])
      || c.version !== 1 || c.type !== 'email.send' || ![c.messageId,c.operationId,c.correlationId,c.deliveryId].every(isId)
      || c.idempotencyKey !== `email-send-${c.deliveryId}` || !shape(c.snapshot, [...snapshotFields])) return false;
    const s = c.snapshot;
    return s.version === 1 && isId(s.planId) && s.workspaceId === workspace && s.connectionId === connection && s.emailAccountId === account
      && Number.isSafeInteger(s.revision) && s.revision >= 0 && address(s.fromAddress)
      && Array.isArray(s.toAddresses) && s.toAddresses.length > 0 && Array.isArray(s.ccAddresses)
      && s.toAddresses.length + s.ccAddresses.length <= 100 && [...s.toAddresses,...s.ccAddresses].every(address)
      && new Set([...s.toAddresses,...s.ccAddresses]).size === s.toAddresses.length + s.ccAddresses.length
      && ['phase9542-accepted','phase9542-late','phase9542-not-sent','phase9542-failed'].includes(s.subject)
      && typeof s.bodyText === 'string' && s.bodyText.length <= 32000 && !/[\x00-\x08\x0b\x0c\x0e-\x1f<>]/.test(s.bodyText)
      && [s.opportunityId,s.companyId,s.contactId].every(x => x === null || isId(x))
      && Buffer.byteLength(JSON.stringify(s)) <= 60000;
  }
  function result(c) {
    if (!validCommand(c)) throw Error('invalid-simulation-command');
    fs.mkdirSync(directory, { recursive: true, mode: 0o700 });
    const file = path.join(directory, `${c.idempotencyKey}.json`);
    const commandHash = crypto.createHash('sha256').update(JSON.stringify(c)).digest('hex');
    const mode = c.snapshot.subject;
    const accepted = mode === 'phase9542-accepted' || mode === 'phase9542-late';
    let record;
    try {
      const fd = fs.openSync(file, 'wx', 0o600);
      try {
        record = { commandHash, command: c, acceptances: accepted ? 1 : 0,
          proof: { version: 1, messageId: crypto.randomUUID(), operationId: c.operationId, correlationId: c.correlationId,
            commandMessageId: c.messageId, type: 'email.send', deliveryId: c.deliveryId, idempotencyKey: c.idempotencyKey,
            outcome: accepted ? 'accepted' : mode === 'phase9542-not-sent' ? 'not-sent' : 'failed',
            providerCode: accepted ? 'local-demo' : null, externalMessageId: accepted ? `simulated-${crypto.randomUUID()}` : null,
            acceptedAt: accepted ? new Date().toISOString() : null } };
        fs.writeFileSync(fd, JSON.stringify(record)); fs.fsyncSync(fd);
      } finally { fs.closeSync(fd); }
    } catch (e) {
      if (e.code !== 'EEXIST') throw e;
      record = JSON.parse(fs.readFileSync(file, 'utf8'));
      if (record.commandHash !== commandHash) throw Error('simulation-key-conflict');
    }
    // Persistent proof survives the deliberately ambiguous response; no callback yet.
    return mode === 'phase9542-late' ? null : record.proof;
  }
  return { type: 'email.send', target: '/webhook/crm-email-send-demo', validCommand, result };
}
class CrmEmailSend {
  constructor() {
    this.description = { displayName: 'CRM simulated outbound email HMAC v1', name: 'crmEmailSend', group: ['transform'], version: 1,
      description: 'Local persistent simulation only; never sends email', defaults: { name: 'Verify and callback' },
      inputs: ['main'], outputs: ['main'], credentials: [{ name: 'crmProbe', required: true }], properties:
        [['accountRef','Local EmailAccount ID'],['workspaceRef','Workspace ID'],['connectionRef','Connection ID']].map(([name,displayName]) =>
          ({ name, displayName, type: 'string', default: '', required: true })) };
  }
  async execute() {
    return executeSignedCommand.call(this, profileFor(this.getNodeParameter('accountRef',0), this.getNodeParameter('workspaceRef',0),
      this.getNodeParameter('connectionRef',0), '/home/node/.n8n/email-send-demo-state'));
  }
}
module.exports = { CrmEmailSend, profileFor };
