'use strict';
const { executeSignedCommand, isId } = require('../shared/CrmLocalHmac');
const shape = (o, keys) => o && typeof o === 'object' && !Array.isArray(o) && Object.keys(o).sort().join(',') === [...keys].sort().join(',');
function events() {
  const common = { revision: 1, description: 'Fictional calendar fixture', location: null, timeZoneId: 'Europe/Paris', status: 'confirmed', seriesId: null, occurrenceId: null };
  return [
    { ...common, externalEventId: 'calendar-demo-001', title: 'Synthetic meeting', startsAt: '2026-10-25T01:30:00+02:00', endsAt: '2026-10-25T02:30:00+02:00', isAllDay: false },
    { ...common, externalEventId: 'calendar-demo-002', title: 'Synthetic all-day', startsAt: '2026-03-29T00:00:00+01:00', endsAt: '2026-03-30T00:00:00+02:00', isAllDay: true },
    { ...common, externalEventId: 'calendar-demo-003', title: 'Synthetic occurrence', startsAt: '2026-10-25T02:30:00+01:00', endsAt: '2026-10-25T03:30:00+01:00', isAllDay: false, seriesId: 'series-demo-001', occurrenceId: 'occurrence-demo-001' }
  ];
}
function profileFor(primary, secondary) {
  if (!isId(primary) || !isId(secondary) || primary === secondary) throw Error('configure-two-distinct-local-calendars');
  function batch(p) {
    const [one, two, three] = events();
    if (p.calendarRef === secondary) {
      if (p.previousCursor === null) return { nextCursor: 'cursor-001', events: [{ ...one, title: 'Independent second calendar' }] };
      if (p.previousCursor === 'cursor-001') return { nextCursor: 'cursor-002', events: [] };
      return null;
    }
    if (p.calendarRef !== primary) return null;
    switch (p.previousCursor) {
      case null: return { nextCursor: 'cursor-001', events: [one, two, three] };
      case 'cursor-001': return { nextCursor: 'cursor-002', events: [one, { ...two, revision: 2, title: 'Updated synthetic all-day' }, { ...three, revision: 2, status: 'cancelled' }] };
      case 'cursor-002': return { nextCursor: 'cursor-003', events: [] };
      case 'cursor-003': return { nextCursor: 'cursor-004', events: [{ ...two, revision: 3, title: 'Must not be partially imported' }, { ...one, title: 'Deliberate same-revision conflict' }] };
      default: return null;
    }
  }
  const validPayload = p => shape(p, ['calendarRef','jobRef','previousCursor','maxItems']) && isId(p.calendarRef) && isId(p.jobRef)
    && (p.previousCursor === null || typeof p.previousCursor === 'string' && p.previousCursor.length <= 2048 && p.previousCursor.length > 0 && p.previousCursor === p.previousCursor.trim() && !p.previousCursor.includes('\0'))
    && Number.isInteger(p.maxItems) && p.maxItems >= 1 && p.maxItems <= 100 && batch(p) !== null && batch(p).events.length <= p.maxItems;
  return { target: '/webhook/crm-calendar-sync-demo', type: 'calendar.sync', payloadFields: ['calendarRef','jobRef','previousCursor','maxItems'],
    validPayload, validCommand: c => shape(c, ['version','messageId','operationId','correlationId','type','payload']) && c.version === 1 && c.type === 'calendar.sync'
      && [c.messageId,c.operationId,c.correlationId].every(isId) && validPayload(c.payload),
    resultPayload: p => ({ calendarRef: p.calendarRef, jobRef: p.jobRef, outcome: 'succeeded', ...batch(p) }) };
}
class CrmCalendarSync {
  constructor() {
    this.description = { displayName: 'CRM deterministic calendar HMAC v1', name: 'crmCalendarSync', group: ['transform'], version: 1,
      description: 'Authenticate calendar.sync and callback with fictional calendar events', defaults: { name: 'Verify and callback' },
      inputs: ['main'], outputs: ['main'], credentials: [{ name: 'crmProbe', required: true }], properties: [
        { displayName: 'Primary local Calendar ID', name: 'primaryCalendarRef', type: 'string', default: '', required: true },
        { displayName: 'Secondary local Calendar ID', name: 'secondaryCalendarRef', type: 'string', default: '', required: true }
      ] };
  }
  async execute() { return executeSignedCommand.call(this, profileFor(this.getNodeParameter('primaryCalendarRef', 0), this.getNodeParameter('secondaryCalendarRef', 0))); }
}
module.exports = { CrmCalendarSync, profileFor, events };
