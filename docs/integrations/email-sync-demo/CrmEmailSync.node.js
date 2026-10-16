'use strict';
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');

// Fictional fixture only. These timestamps and contents never depend on execution time.
function message(number) {
  const names = ['alice', 'bob', 'carol'], ordinals = ['first', 'second', 'third'];
  const suffix = String(number).padStart(3, '0');
  return { externalMessageId: `mail-demo-${suffix}`, externalThreadId: `thread-demo-${suffix}`,
    fromAddress: `${names[number - 1]}@example.test`, toAddresses: ['demo-inbox@example.test'],
    ccAddresses: number === 2 ? ['team@example.test'] : [],
    subject: `Phase 9.5.2 ${ordinals[number - 1]} deterministic message`,
    bodyText: `Inbound deterministic email message ${number}`, occurredAt: `2026-10-16T08:0${number}:00Z` };
}
function profileFor(primary, secondary) {
  if (!isId(primary) || !isId(secondary) || primary === secondary) throw new Error('configure-two-distinct-local-accounts');
  function batch(p) {
    if (p.accountRef === secondary) {
      if (p.previousCursor === null) return { nextCursor: 'cursor-001', messages: [{ ...message(1),
        toAddresses: ['demo-inbox-2@example.test'], subject: 'Phase 9.5.2 second account message',
        bodyText: 'Inbound deterministic email for account 2' }] };
      if (p.previousCursor === 'cursor-001') return { nextCursor: 'cursor-002', messages: [] };
      return null;
    }
    if (p.accountRef !== primary) return null;
    switch (p.previousCursor) {
      case null: return { nextCursor: 'cursor-001', messages: [message(1), message(2)] };
      case 'cursor-001': return { nextCursor: 'cursor-002', messages: [message(1), message(3)] };
      case 'cursor-002': return { nextCursor: 'cursor-003', messages: [] };
      case 'cursor-003': return { nextCursor: 'cursor-004', messages: [{ ...message(1), bodyText: 'Deliberately divergent content' }] };
      default: return null;
    }
  }
  return {
    target: '/webhook/crm-email-sync-demo', type: 'email.sync',
    payloadFields: ['accountRef', 'previousCursor', 'maxItems'],
    validPayload: p => isId(p.accountRef) && (p.previousCursor === null || typeof p.previousCursor === 'string')
      && Number.isInteger(p.maxItems) && p.maxItems >= 1 && p.maxItems <= 100
      && batch(p) !== null && batch(p).messages.length <= p.maxItems,
    resultPayload: p => ({ accountRef: p.accountRef, outcome: 'succeeded', ...batch(p) })
  };
}
class CrmEmailSync {
  constructor() {
    this.description = { displayName: 'CRM deterministic inbound email HMAC v1', name: 'crmEmailSync', group: ['transform'], version: 1,
      description: 'Verify a raw email.sync command and return fictional inbound messages', defaults: { name: 'Verify and callback' },
      inputs: ['main'], outputs: ['main'], credentials: [{ name: 'crmProbe', required: true }], properties: [
        { displayName: 'Primary local EmailAccount ID', name: 'primaryAccountRef', type: 'string', default: '', required: true },
        { displayName: 'Secondary local EmailAccount ID', name: 'secondaryAccountRef', type: 'string', default: '', required: true }
      ] };
  }
  async execute() {
    return executeSignedCommand.call(this, profileFor(this.getNodeParameter('primaryAccountRef', 0), this.getNodeParameter('secondaryAccountRef', 0)));
  }
}
module.exports = { CrmEmailSync, profileFor };
