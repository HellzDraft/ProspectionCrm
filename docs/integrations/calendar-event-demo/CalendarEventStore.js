'use strict';
const fs = require('node:fs'), path = require('node:path'), crypto = require('node:crypto');
const { DatabaseSync } = require('node:sqlite');
const { isId } = require('../shared/CrmLocalHmac');
const { hash, date, eventHash, validEvent, validCommand } = require('./CalendarEventContract');
const modes = ['apply','withhold-callback','not-applied','unknown'];
function scopeValid(s) {
  return ['workspaceId','connectionId','calendarAccountId','calendarId'].every(k => isId(s[k]))
    && s.providerCode === 'local-demo' && typeof s.externalCalendarId === 'string' && s.externalCalendarId.length > 0 && s.externalCalendarId.length <= 500;
}
function configure(db) {
  db.exec('PRAGMA busy_timeout=5000; PRAGMA foreign_keys=ON; PRAGMA synchronous=EXTRA; PRAGMA trusted_schema=OFF;');
  if (db.prepare('PRAGMA journal_mode').get().journal_mode !== 'delete' || db.prepare('PRAGMA synchronous').get().synchronous !== 3
    || db.prepare('PRAGMA foreign_keys').get().foreign_keys !== 1 || db.prepare('PRAGMA quick_check').get().quick_check !== 'ok') throw Error('unsafe-or-corrupt-registry');
}
function normalizeEvent(e) {
  if (!validEvent(e)) throw Error('invalid-provider-fixture');
  return { ...e, startsAt:date(e.startsAt).json, endsAt:date(e.endsAt).json };
}
class CalendarEventStore {
  static initialize(file, scopes, fixtures = []) {
    if (!path.isAbsolute(file) || !scopes.length || !scopes.every(scopeValid)) throw Error('invalid-registry-configuration');
    fs.mkdirSync(path.dirname(file), {recursive:true,mode:0o700});
    const fd = fs.openSync(file,'wx',0o600); fs.closeSync(fd); // Existing or incomplete registries are never reset.
    const db = new DatabaseSync(file);
    try {
      db.exec('PRAGMA journal_mode=DELETE;'); configure(db); db.exec('BEGIN IMMEDIATE;');
      db.exec(`CREATE TABLE metadata(version INTEGER NOT NULL CHECK(version=1)); INSERT INTO metadata VALUES(1);
        CREATE TABLE calendars(calendar_ref TEXT PRIMARY KEY, scope_json TEXT NOT NULL) STRICT;
        CREATE TABLE events(calendar_ref TEXT NOT NULL REFERENCES calendars(calendar_ref), external_id TEXT NOT NULL,
          value_json TEXT NOT NULL, PRIMARY KEY(calendar_ref,external_id)) STRICT;
        CREATE TABLE policies(plan_id TEXT PRIMARY KEY, mode TEXT NOT NULL CHECK(mode IN ('apply','withhold-callback','not-applied','unknown'))) STRICT;
        CREATE TABLE ledger(idempotency_key TEXT PRIMARY KEY, command_json TEXT NOT NULL, command_hash TEXT NOT NULL,
          proof_json TEXT NOT NULL, mode TEXT NOT NULL) STRICT;
        CREATE TABLE effects(idempotency_key TEXT PRIMARY KEY REFERENCES ledger(idempotency_key), calendar_ref TEXT NOT NULL REFERENCES calendars(calendar_ref),
          external_id TEXT NOT NULL, before_json TEXT, after_json TEXT NOT NULL) STRICT;`);
      for (const s of scopes) db.prepare('INSERT INTO calendars VALUES(?,?)').run(s.calendarId,JSON.stringify(s));
      for (const f of fixtures) { const e = normalizeEvent(f.event); db.prepare('INSERT INTO events VALUES(?,?,?)').run(f.calendarId,e.externalEventId,JSON.stringify(e)); }
      db.exec('COMMIT;');
    } finally { db.close(); }
  }
  constructor(file) {
    if (!path.isAbsolute(file) || !fs.existsSync(file) || !fs.lstatSync(file).isFile() || fs.lstatSync(file).isSymbolicLink()) throw Error('registry-missing-or-unsafe');
    this.db = new DatabaseSync(file);
    try { configure(this.db); if (this.db.prepare('SELECT version FROM metadata').all().length !== 1
      || this.db.prepare('SELECT version FROM metadata').get().version !== 1) throw Error('registry-version'); }
    catch(e) { this.db.close(); throw e; }
  }
  close() { this.db.close(); }
  transaction(action) {
    this.db.exec('BEGIN IMMEDIATE;');
    try { const result = action(); this.db.exec('COMMIT;'); return result; }
    catch(e) { try { this.db.exec('ROLLBACK;'); } catch { /* Preserve the original failure; never claim success. */ } throw e; }
  }
  scope(calendar) { const row = this.db.prepare('SELECT scope_json FROM calendars WHERE calendar_ref=?').get(calendar); return row ? JSON.parse(row.scope_json) : null; }
  event(calendar,id) { const row = this.db.prepare('SELECT value_json FROM events WHERE calendar_ref=? AND external_id=?').get(calendar,id); return row ? JSON.parse(row.value_json) : null; }
  lookup(key) {
    const row = this.db.prepare('SELECT proof_json FROM ledger WHERE idempotency_key=?').get(key);
    return row ? JSON.parse(row.proof_json) : null; // Read only: no command execution, effect or callback.
  }
  counts() { return { ledger:this.db.prepare('SELECT count(*) AS n FROM ledger').get().n, effects:this.db.prepare('SELECT count(*) AS n FROM effects').get().n,
    events:this.db.prepare('SELECT count(*) AS n FROM events').get().n }; }
  // Private administrator fixtures. These functions are never reachable from the workflow payload.
  policy(plan,mode) {
    if (!isId(plan) || !modes.includes(mode)) throw Error('invalid-fixture-policy');
    this.transaction(() => this.db.prepare('INSERT INTO policies VALUES(?,?)').run(plan,mode));
  }
  advance(calendar,id,expectedRevision,value) {
    const next = normalizeEvent(value);
    this.transaction(() => {
      const old = this.event(calendar,id);
      if (!old || old.revision !== expectedRevision || next.externalEventId !== id || next.revision <= old.revision
        || old.seriesId !== next.seriesId || old.occurrenceId !== next.occurrenceId) throw Error('fixture-version-conflict');
      this.db.prepare('UPDATE events SET value_json=? WHERE calendar_ref=? AND external_id=?').run(JSON.stringify(next),calendar,id);
    });
  }
  execute(command, checkpoint = () => {}) {
    return this.transaction(() => {
      const c = command, s = c?.snapshot, content = s?.content, scope = this.scope(content?.calendarId ?? '');
      if (!scope || !validCommand(c,scope)) throw Error('invalid-simulation-command');
      const canonicalCommand = JSON.stringify(c), commandHash = hash(canonicalCommand);
      const prior = this.db.prepare('SELECT * FROM ledger WHERE idempotency_key=?').get(c.idempotencyKey);
      if (prior) {
        if (prior.command_hash !== commandHash || prior.command_json !== canonicalCommand) throw Error('simulation-key-conflict');
        return { proof:JSON.parse(prior.proof_json), withhold:prior.mode === 'withhold-callback' };
      }
      const mode = this.db.prepare('SELECT mode FROM policies WHERE plan_id=?').get(s.planId)?.mode ?? 'apply';
      let outcome = mode === 'not-applied' ? 'not-applied' : mode === 'unknown' ? 'unknown' : 'applied';
      let observedRevision = null, diagnosticCode = mode === 'not-applied' ? 'SyntheticRefusal' : mode === 'unknown' ? 'SyntheticUncertainty' : null;
      let old = null, applied = null;
      if (outcome === 'applied' && c.precondition) {
        const p = c.precondition;
        old = this.event(content.calendarId,p.externalEventId); // Never seeded from the incoming update.
        if (!old) { outcome='not-applied'; diagnosticCode='TargetMissing'; }
        else if (old.revision !== p.expectedRevision) { outcome='version-conflict'; observedRevision=old.revision; diagnosticCode='VersionMismatch'; }
        else if (eventHash(old) !== p.targetContentHash || old.status !== 'confirmed'
          || old.seriesId !== s.target.seriesId || old.occurrenceId !== s.target.occurrenceId) { outcome='not-applied'; diagnosticCode='TargetContentMismatch'; }
      }
      if (outcome === 'applied') {
        applied = normalizeEvent({ externalEventId:old?.externalEventId ?? 'simulated-calendar-'+crypto.randomUUID(), revision:old ? old.revision+1 : 1,
          title:content.title,description:content.description,location:content.location,startsAt:content.startsAt,endsAt:content.endsAt,
          isAllDay:content.isAllDay,timeZoneId:content.timeZoneId,status:'confirmed',seriesId:old?.seriesId ?? null,occurrenceId:old?.occurrenceId ?? null });
        this.db.prepare('INSERT INTO events VALUES(?,?,?) ON CONFLICT(calendar_ref,external_id) DO UPDATE SET value_json=excluded.value_json')
          .run(content.calendarId,applied.externalEventId,JSON.stringify(applied));
      }
      checkpoint('after-event-before-ledger'); // Tests can terminate a child process inside the real transaction.
      const proof = { version:1,messageId:crypto.randomUUID(),operationId:c.operationId,correlationId:c.correlationId,commandMessageId:c.messageId,
        type:c.type,deliveryId:c.deliveryId,idempotencyKey:c.idempotencyKey,contentHash:c.contentHash,calendarId:content.calendarId,
        externalCalendarId:c.externalCalendarId,providerCode:c.providerCode,outcome,provenAt:outcome === 'unknown' ? null : new Date().toISOString(),
        event:applied,observedRevision,diagnosticCode };
      if (Buffer.byteLength(JSON.stringify(proof)) > 65536) throw Error('provider-proof-too-large');
      this.db.prepare('INSERT INTO ledger VALUES(?,?,?,?,?)').run(c.idempotencyKey,canonicalCommand,commandHash,JSON.stringify(proof),mode);
      if (applied) this.db.prepare('INSERT INTO effects VALUES(?,?,?,?,?)').run(c.idempotencyKey,content.calendarId,applied.externalEventId,old ? JSON.stringify(old) : null,JSON.stringify(applied));
      checkpoint('before-commit');
      return { proof,withhold:mode === 'withhold-callback' };
    });
  }
}
module.exports = { CalendarEventStore };
