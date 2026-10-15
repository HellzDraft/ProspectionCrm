'use strict';
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');
const profile = {
  target: '/webhook/crm-remote-collection-demo', type: 'collection.request',
  payloadFields: ['requestId', 'query', 'maxItems'],
  validPayload: p => isId(p.requestId) && p.query === 'phase-9-4-3-demo' && Number.isInteger(p.maxItems) && p.maxItems >= 2 && p.maxItems <= 100,
  resultPayload: p => ({ requestId: p.requestId, outcome: 'succeeded', items: [
    { externalId: 'remote-demo-001', title: 'Remote Demo .NET Backend', location: 'Bordeaux',
      description: 'Phase 9.4.3 deterministic remote collection candidate 1' },
    { externalId: 'remote-demo-002', title: 'Remote Demo Unity Tools', location: 'Bordeaux',
      description: 'Phase 9.4.3 deterministic remote collection candidate 2' }
  ] })
};
class CrmCollection {
  constructor() {
    this.description = { displayName: 'CRM deterministic collection HMAC v1', name: 'crmCollection', group: ['transform'], version: 1,
      description: 'Verify a signed raw collection command and return exactly two fictional candidates', defaults: { name: 'Verify and callback' },
      inputs: ['main'], outputs: ['main'], credentials: [{ name: 'crmProbe', required: true }], properties: [] };
  }
  async execute() { return executeSignedCommand.call(this, profile); }
}
module.exports = { CrmCollection };
