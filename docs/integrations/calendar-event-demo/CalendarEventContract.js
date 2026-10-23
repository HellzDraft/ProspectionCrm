'use strict';
const crypto = require('node:crypto');
const { isId } = require('../shared/CrmLocalHmac');
const fields = {
  command: ['version','messageId','operationId','correlationId','type','deliveryId','idempotencyKey','contentHash','providerCode','externalCalendarId','snapshot','precondition','notifications'],
  snapshot: ['version','planId','workspaceId','calendarAccountId','connectionId','revision','content','target'],
  content: ['calendarId','operationCode','targetCalendarEventId','title','description','location','startsAt','endsAt','isAllDay','timeZoneId','opportunityId','companyId','contactId'],
  target: ['id','providerCode','externalEventId','revision','contentHash','title','description','location','startsAt','endsAt','isAllDay','timeZoneId','statusCode','seriesId','occurrenceId','opportunityId','contactId'],
  precondition: ['calendarId','externalCalendarId','externalEventId','expectedRevision','targetContentHash'],
  event: ['externalEventId','revision','title','description','location','startsAt','endsAt','isAllDay','timeZoneId','status','seriesId','occurrenceId']
};
const shape = (o, names) => o && typeof o === 'object' && !Array.isArray(o) && Object.keys(o).sort().join(',') === [...names].sort().join(',');
const hash = s => crypto.createHash('sha256').update(s).digest('hex');
const hex = s => typeof s === 'string' && /^[0-9a-f]{64}$/.test(s);
const integer = n => Number.isSafeInteger(n) && n >= 0;
const nullableId = s => s === null || isId(s);
const text = (s, max, nullable = false, allowEmpty = false) => s === null ? nullable : typeof s === 'string'
  && s.length <= max && !s.includes('\0') && (allowEmpty || s.trim().length > 0 && s.trim() === s);

// System.Text.Json's default encoder and record property order are part of the approved hash.
// DateTimeOffset properties use its date writer (unescaped '+'); content dates are strings.
function quote(s) {
  let out = '"';
  for (let i = 0; i < s.length; i++) {
    const n = s.charCodeAt(i), c = s[i];
    if (c === '\\') out += '\\\\';
    else if ('\b\t\n\f\r'.includes(c)) out += { '\b':'\\b','\t':'\\t','\n':'\\n','\f':'\\f','\r':'\\r' }[c];
    else if (n < 32 || n > 126 || '"+`&<>\''.includes(c)) out += '\\u' + n.toString(16).toUpperCase().padStart(4,'0');
    else out += c;
  }
  return out + '"';
}
function canonical(o, dateProperties = false) {
  if (o === null || typeof o === 'number' || typeof o === 'boolean') return JSON.stringify(o);
  if (typeof o === 'string') return quote(o);
  return '{' + Object.entries(o).map(([k,v]) => quote(k) + ':' + (dateProperties && ['startsAt','endsAt','StartsAt','EndsAt'].includes(k)
    ? JSON.stringify(date(v).json) : canonical(v, k === 'target'))).join(',') + '}';
}
function date(value) {
  if (typeof value !== 'string') throw Error('invalid-date');
  const m = /^(\d{4})-(\d{2})-(\d{2})T(\d{2}):(\d{2}):(\d{2})(?:\.(\d{1,7}))?(Z|[+-]\d{2}:\d{2})$/.exec(value);
  if (!m || +m[1] < 1 || +m[2] < 1 || +m[2] > 12 || +m[3] < 1 || +m[3] > new Date(Date.UTC(+m[1],+m[2],0)).getUTCDate()
    || +m[4] > 23 || +m[5] > 59 || +m[6] > 59) throw Error('invalid-date');
  const offset = m[8] === 'Z' ? 0 : (m[8][0] === '-' ? -1 : 1) * (+m[8].slice(1,3) * 60 + +m[8].slice(4));
  if (Math.abs(offset) > 840 || m[8] !== 'Z' && +m[8].slice(4) > 59) throw Error('invalid-offset');
  const fraction = (m[7] || '').padEnd(7,'0');
  if (fraction[6] !== '0') throw Error('sub-microsecond-not-canonical');
  const ms = Date.parse(value.slice(0,19) + '.000' + m[8]);
  if (!Number.isFinite(ms)) throw Error('invalid-date');
  const base = new Date(ms).toISOString().slice(0,19), micro = fraction.slice(0,6), trimmed = micro.replace(/0+$/,'');
  return { ms, micros: BigInt(ms) * 1000n + BigInt(micro), offset, json: base + (trimmed ? '.'+trimmed : '') + '+00:00',
    content: base + '.'+fraction + '+00:00' };
}
function validEvent(e) {
  try {
    if (!shape(e, fields.event) || !text(e.externalEventId,500) || !integer(e.revision) || !text(e.title,500)
      || !text(e.description,10000,true,true) || !text(e.location,500,true,true) || typeof e.isAllDay !== 'boolean'
      || !['confirmed','cancelled'].includes(e.status) || !text(e.timeZoneId,100,true)
      || !text(e.seriesId,500,true) || !text(e.occurrenceId,500,true) || (e.seriesId === null) !== (e.occurrenceId === null)) return false;
    const a = date(e.startsAt), b = date(e.endsAt);
    if (b.micros <= a.micros) return false;
    if (e.timeZoneId === null) return !e.isAllDay;
    // Explicit IANA identifiers only, matching the demo's declared scope (no Windows zone aliases).
    if (e.timeZoneId !== 'UTC' && !e.timeZoneId.includes('/')) return false;
    const fmt = new Intl.DateTimeFormat('en-GB', { timeZone: e.timeZoneId, year:'numeric',month:'2-digit',day:'2-digit',hour:'2-digit',minute:'2-digit',second:'2-digit',hourCycle:'h23' });
    for (const d of [a,b]) {
      const p = Object.fromEntries(fmt.formatToParts(new Date(d.ms)).map(x => [x.type,x.value]));
      const offset = (Date.UTC(+p.year,+p.month-1,+p.day,+p.hour,+p.minute,+p.second) - d.ms) / 60000;
      if (d.offset !== 0 && d.offset !== offset) return false;
      if (e.isAllDay && (p.hour !== '00' || p.minute !== '00' || p.second !== '00' || d.micros % 1000000n !== 0n)) return false;
    }
    return true;
  } catch { return false; }
}
function eventFromTarget(t) {
  return { externalEventId:t.externalEventId,revision:t.revision,title:t.title,description:t.description,location:t.location,
    startsAt:t.startsAt,endsAt:t.endsAt,isAllDay:t.isAllDay,timeZoneId:t.timeZoneId,status:t.statusCode,seriesId:t.seriesId,occurrenceId:t.occurrenceId };
}
function eventHash(e) {
  const ordered = Object.fromEntries(fields.event.map(k => [k[0].toUpperCase()+k.slice(1),e[k]]));
  return hash(canonical(ordered,true));
}
function snapshotText(s) {
  const ordered = Object.fromEntries(fields.snapshot.map(k => [k, k === 'content' ? Object.fromEntries(fields.content.map(f => [f,s.content[f]]))
    : k === 'target' && s.target !== null ? Object.fromEntries(fields.target.map(f => [f,s.target[f]])) : s[k]]));
  return canonical(ordered);
}
function validCommand(c, scope) {
  try {
    if (!shape(c,fields.command) || c.version !== 1 || ![c.messageId,c.operationId,c.correlationId,c.deliveryId].every(isId)
      || !['calendar.event.create','calendar.event.update'].includes(c.type) || c.idempotencyKey !== `calendar-event-${c.deliveryId}`
      || !hex(c.contentHash) || c.notifications !== 'disabled' || c.providerCode !== 'local-demo' || c.providerCode !== scope.providerCode
      || c.externalCalendarId !== scope.externalCalendarId || !shape(c.snapshot,fields.snapshot)) return false;
    const s = c.snapshot, p = s.content, t = s.target;
    if (s.version !== 1 || !isId(s.planId) || !integer(s.revision) || s.workspaceId !== scope.workspaceId
      || s.connectionId !== scope.connectionId || s.calendarAccountId !== scope.calendarAccountId || !shape(p,fields.content)
      || p.calendarId !== scope.calendarId || c.type !== 'calendar.event.'+p.operationCode
      || ![p.targetCalendarEventId,p.opportunityId,p.companyId,p.contactId].every(nullableId)) return false;
    const e = { externalEventId:'plan',revision:0,title:p.title,description:p.description,location:p.location,startsAt:p.startsAt,endsAt:p.endsAt,
      isAllDay:p.isAllDay,timeZoneId:p.timeZoneId,status:'confirmed',seriesId:null,occurrenceId:null };
    if (!validEvent(e) || p.startsAt !== date(p.startsAt).content || p.endsAt !== date(p.endsAt).content) return false;
    if (p.operationCode === 'create') { if (t !== null || c.precondition !== null || p.targetCalendarEventId !== null) return false; }
    else {
      if (!shape(t,fields.target) || t.id !== p.targetCalendarEventId || !isId(t.id) || t.providerCode !== c.providerCode
        || ![t.opportunityId,t.contactId].every(nullableId) || !validEvent(eventFromTarget(t)) || t.statusCode !== 'confirmed'
        || !hex(t.contentHash) || eventHash(eventFromTarget(t)) !== t.contentHash || !shape(c.precondition,fields.precondition)) return false;
      const expected = { calendarId:p.calendarId,externalCalendarId:c.externalCalendarId,externalEventId:t.externalEventId,expectedRevision:t.revision,targetContentHash:t.contentHash };
      if (fields.precondition.some(k => c.precondition[k] !== expected[k])) return false;
    }
    const json = snapshotText(s);
    return Buffer.byteLength(json) <= 60000 && hash(json) === c.contentHash && Buffer.byteLength(JSON.stringify(c)) <= 65536;
  } catch { return false; }
}
module.exports = { fields, hash, shape, date, canonical, snapshotText, eventHash, eventFromTarget, validEvent, validCommand };
