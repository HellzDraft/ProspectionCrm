'use strict';
class CrmProbe {
  constructor() {
    this.name = 'crmProbe'; this.displayName = 'CRM local technical probe';
    this.properties = [
      { displayName: 'CRM to n8n key ID', name: 'incomingId', type: 'string', default: '' },
      { displayName: 'CRM to n8n secret Base64', name: 'incomingSecret', type: 'string', typeOptions: { password: true }, default: '' },
      { displayName: 'n8n to CRM key ID', name: 'outgoingId', type: 'string', default: '' },
      { displayName: 'n8n to CRM secret Base64', name: 'outgoingSecret', type: 'string', typeOptions: { password: true }, default: '' },
      { displayName: 'Trusted localhost certificate (public PEM)', name: 'caPem', type: 'string', typeOptions: { rows: 8 }, default: '' }
    ];
  }
}
module.exports = { CrmProbe };
