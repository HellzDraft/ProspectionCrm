'use strict';
const crypto = require('crypto');
const https = require('https');
const dns = require('dns');
const callbackTarget = '/api/integrations/results';
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/i;
const isId = v => typeof v === 'string' && uuid.test(v) && v !== '00000000-0000-0000-0000-000000000000';
const shape = (obj, fields) => obj && !Array.isArray(obj) && typeof obj === 'object' && Object.keys(obj).sort().join(',') === fields.sort().join(',');
const sign = (key, audience, id, timestamp, path, body) => crypto.createHmac('sha256', key).update(['crm-hmac-v1', audience, id, timestamp, 'POST', path, crypto.createHash('sha256').update(body).digest('hex')].join('\n')).digest();
function post(body, headers, ca) {
  return new Promise((resolve, reject) => {
    const req = https.request({ hostname: 'localhost', port: 7096, path: callbackTarget, method: 'POST',
      ca, rejectUnauthorized: true, minVersion: 'TLSv1.2', timeout: 8000,
      lookup: (_hostname, options, cb) => dns.lookup('host.docker.internal', options, cb),
      headers: { 'Content-Type': 'application/json; charset=utf-8', 'Content-Length': body.length, ...headers }
    }, res => {
      const chunks = []; let size = 0; const authorized = res.socket.authorized;
      res.on('data', chunk => { size += chunk.length; if (size > 4096) req.destroy(new Error('callback-too-large')); else chunks.push(chunk); });
      res.on('end', () => resolve({ status: res.statusCode, authorized, body: Buffer.concat(chunks).toString('utf8') }));
      res.on('error', () => reject(new Error('callback-response-error')));
    });
    req.on('timeout', () => req.destroy(new Error('callback-timeout')));
    req.on('error', () => reject(new Error('callback-network-or-tls-error')));
    req.end(body);
  });
}
// Shared local protocol; credentials remain inside n8n and are never emitted.
async function executeSignedCommand(profile) {
    const reject = (responseCode, error) => [[{ json: { responseCode, error } }]];
    const items = this.getInputData();
    if (items.length !== 1) return reject(400, 'one-command-required');
    const c = await this.getCredentials('crmProbe');
    const incoming = Buffer.from(c.incomingSecret, 'base64'), outgoing = Buffer.from(c.outgoingSecret, 'base64');
    if (incoming.length !== 32 || outgoing.length !== 32 || crypto.timingSafeEqual(incoming, outgoing)) throw new Error('invalid-key-configuration');
    const h = items[0].json.headers || {}, timestamp = h['x-crm-timestamp'], signature = h['x-crm-signature'];
    if (h['x-crm-key-id'] !== c.incomingId || !/^[A-Za-z0-9_-]{1,64}$/.test(c.incomingId) ||
      typeof timestamp !== 'string' || !/^[0-9]{10}$/.test(timestamp) || Math.abs(Math.floor(Date.now()/1000) - Number(timestamp)) > 300 ||
      typeof signature !== 'string' || !/^[0-9a-f]{64}$/.test(signature)) return reject(401, 'invalid-signature');
    if (h['content-encoding'] || !/^application\/json(?:;\s*charset=utf-8)?$/i.test(h['content-type'] || '') || Object.keys(items[0].json.query || {}).length) return reject(415, 'unsupported-body');
    if (!items[0].binary?.data) return reject(400, 'raw-body-required');
    const raw = await this.helpers.getBinaryDataBuffer(0, 'data');
    const emailSend = profile.type === 'email.send';
    if (!raw.length || raw.length > (emailSend ? 65536 : profile.type === 'calendar.sync' ? 16384 : 4096)) return reject(413, 'invalid-body-size');
    if (!crypto.timingSafeEqual(sign(incoming, 'crm-to-n8n', c.incomingId, timestamp, profile.target, raw), Buffer.from(signature, 'hex'))) return reject(401, 'invalid-signature');
    let command;
    try { command = JSON.parse(new TextDecoder('utf-8', { fatal: true }).decode(raw)); }
    catch { return reject(400, 'invalid-command'); }
    // JSON duplicate properties are rejected independently of the parsed object.
    const propertyNames = emailSend || profile.type === 'calendar.sync' ? [] : [...raw.toString('utf8').matchAll(/"((?:[^"\\]|\\.)*)"\s*:/g)].map(m => JSON.parse('"' + m[1] + '"'));
    if (emailSend || profile.type === 'calendar.sync' ? (!uniqueProperties(raw.toString('utf8')) || !profile.validCommand(command)) :
      (propertyNames.length !== 6 + profile.payloadFields.length || new Set(propertyNames).size !== propertyNames.length ||
      !shape(command, ['version','messageId','operationId','correlationId','type','payload']) || command.version !== 1 || command.type !== profile.type ||
      ![command.messageId,command.operationId,command.correlationId].every(isId) || !shape(command.payload, [...profile.payloadFields]) || !profile.validPayload(command.payload))) return reject(400, 'invalid-command');
    const result = emailSend ? await profile.result(command) : { version: command.version, messageId: crypto.randomUUID(), operationId: command.operationId, correlationId: command.correlationId,
      commandMessageId: command.messageId, type: profile.type, payload: profile.resultPayload(command.payload) };
    if (result === null) return [[{ json: { responseCode: 503, signatureValidated: true, operationId: command.operationId } }]];
    const signatureValidatedAt = new Date().toISOString();
    return [[{ json: { responseCode: 202, signatureValidated: true, signatureValidatedAt, rawBytes: raw.length,
      rawSha256: crypto.createHash('sha256').update(raw).digest('hex'), ...await sendCallback(c, result), operationId: command.operationId } }]];
}
async function sendCallback(c, result) {
  const body = Buffer.from(JSON.stringify(result), 'utf8'), callbackTimestamp = String(Math.floor(Date.now()/1000));
  const key = Buffer.from(c.outgoingSecret, 'base64');
  if (key.length !== 32 || !/^[A-Za-z0-9_-]{1,64}$/.test(c.outgoingId)) throw Error('invalid-key-configuration');
  if ((result.type === 'email.send' && body.length > 4096) || (result.type === 'calendar.sync' && body.length > 524288)) throw Error('callback-too-large');
  const callback = await post(body, { 'X-CRM-Key-Id': c.outgoingId, 'X-CRM-Timestamp': callbackTimestamp,
    'X-CRM-Signature': sign(key, 'n8n-to-crm', c.outgoingId, callbackTimestamp, callbackTarget, body).toString('hex') }, c.caPem);
  let ack; try { ack = JSON.parse(callback.body); } catch { throw new Error('invalid-callback-response'); }
  if (!callback.authorized || callback.status !== 202 || ack.status !== 'received' || typeof ack.alreadyRecorded !== 'boolean') throw new Error('callback-not-accepted');
  return { callbackTlsValidated: true, callbackStatus: callback.status, callbackTimestamp,
    callbackBody: body.toString('utf8'), callbackAlreadyRecorded: ack.alreadyRecorded };
}
// Called only after JSON.parse succeeded: tokenize strings atomically, track object-local names.
function uniqueProperties(json) {
  const tokens = json.match(/"(?:[^"\\]|\\.)*"|[{}\[\]:,]|[^\s{}\[\]:,]+/g) || [];
  const stack = [];
  for (let i = 0; i < tokens.length; i++) {
    const t = tokens[i];
    if (t === '{' || t === '[') { stack.push(t === '{' ? new Set() : null); if (stack.length > 6) return false; }
    else if (t === '}' || t === ']') stack.pop();
    else if (t.startsWith('"') && tokens[i + 1] === ':') {
      const names = stack[stack.length - 1], name = JSON.parse(t);
      if (!names || names.has(name)) return false;
      names.add(name);
    }
  }
  return true;
}
module.exports = { executeSignedCommand, isId, uniqueProperties, sendCallback };
