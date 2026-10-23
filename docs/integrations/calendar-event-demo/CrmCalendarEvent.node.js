'use strict';
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');
const { validCommand } = require('./CalendarEventContract');
const { CalendarEventStore } = require('./CalendarEventStore');
const registry = '/home/node/.n8n/calendar-event-demo-state/provider.sqlite';
function profileFor(scope, file = registry) {
  if (!['workspaceId','connectionId','calendarAccountId','calendarId'].every(k => isId(scope[k]))
    || scope.providerCode !== 'local-demo' || !scope.externalCalendarId) throw Error('configure-local-calendar-scope');
  return { type:'calendar.event',target:'/webhook/crm-calendar-event-demo',validCommand:c => validCommand(c,scope),
    result(c) {
      const store = new CalendarEventStore(file);
      try { const result = store.execute(c); return result.withhold ? null : result.proof; } finally { store.close(); }
    } };
}
class CrmCalendarEvent {
  constructor() {
    this.description = { displayName:'CRM simulated calendar event HMAC v1',name:'crmCalendarEvent',group:['transform'],version:1,
      description:'Durable local calendar simulation only; no provider or invitations',defaults:{name:'Verify, simulate and callback'},
      inputs:['main'],outputs:['main'],credentials:[{name:'crmProbe',required:true}],properties:
        [['workspaceId','Workspace ID'],['connectionId','Connection ID'],['calendarAccountId','Local CalendarAccount ID'],
          ['calendarId','Local Calendar ID'],['externalCalendarId','Fictional external calendar ID']].map(([name,displayName]) =>
          ({name,displayName,type:'string',default:'',required:true})) };
  }
  async execute() {
    const scope = Object.fromEntries(['workspaceId','connectionId','calendarAccountId','calendarId','externalCalendarId'].map(k => [k,this.getNodeParameter(k,0)]));
    return executeSignedCommand.call(this,profileFor({...scope,providerCode:'local-demo'}));
  }
}
module.exports = { CrmCalendarEvent, profileFor };
