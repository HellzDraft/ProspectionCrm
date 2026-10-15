'use strict';
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');
const profile = {
  target: '/webhook/crm-technical-probe', type: 'technical.probe', payloadFields: ['probeId'],
  validPayload: p => isId(p.probeId),
  resultPayload: p => ({ probeId: p.probeId, outcome: 'succeeded' })
};
class CrmProbe {
  constructor() {
    this.description = { displayName: 'CRM technical.probe HMAC v1', name: 'crmProbe', group: ['transform'], version: 1,
      description: 'Verify raw CRM bytes and send one signed HTTPS technical result', defaults: { name: 'Verify and callback' },
      inputs: ['main'], outputs: ['main'], credentials: [{ name: 'crmProbe', required: true }], properties: [] };
  }
  async execute() { return executeSignedCommand.call(this, profile); }
}
module.exports = { CrmProbe };
