// Run from the repository root: node --test docs/SheetBoard/tests/appsscript.test.cjs
// Apps Script and GitHub are mocked; these tests never publish or send mail.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');

const script = fs.readFileSync(path.resolve(__dirname, '../../../Packages/com.tentee.vrc-sheet-board/AppsScript/SheetBoard.gs'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

function properties(initial = {}) {
  const data = {...initial};
  return {
    data,
    getProperty: key => Object.hasOwn(data, key) ? data[key] : null,
    setProperty(key, value) { data[key] = String(value); return this; },
    setProperties(values) { Object.entries(values).forEach(([key, value]) => this.setProperty(key, value)); return this; },
    deleteProperty(key) { delete data[key]; return this; },
    getProperties: () => ({...data})
  };
}

function harness({user = {token: 'test-token', login: 'admin'}, doc = {}, fetch, locked = false} = {}) {
  const users = properties(user), shared = properties(doc);
  const requests = [], mails = [], triggers = [], dialogs = [], lockWaits = [];
  let releases = 0, uuid = 0;
  const spreadsheet = {getName: () => 'テストシート', getUrl: () => 'https://example.test/sheet', getSpreadsheetTimeZone: () => 'Asia/Tokyo'};
  const context = vm.createContext({
    PropertiesService: {getUserProperties: () => users, getDocumentProperties: () => shared},
    LockService: {getDocumentLock: () => ({tryLock: ms => { lockWaits.push(ms); return !locked; }, releaseLock: () => releases++})},
    Utilities: {
      getUuid: () => 'pending-' + ++uuid,
      DigestAlgorithm: {MD5: 'md5'}, Charset: {UTF_8: 'utf8'},
      computeDigest: (_, text) => Array.from(crypto.createHash('md5').update(text, 'utf8').digest()),
      newBlob: text => ({getBytes: () => Buffer.from(text, 'utf8')}),
      formatDate: () => '+0900'
    },
    SpreadsheetApp: {getActiveSpreadsheet: () => spreadsheet},
    Session: {getEffectiveUser: () => ({getEmail: () => 'admin@example.test'}), getScriptTimeZone: () => 'Asia/Tokyo'},
    MailApp: {sendEmail: (...args) => mails.push(args)},
    ScriptApp: {
      getProjectTriggers: () => triggers.slice(),
      deleteTrigger: trigger => triggers.splice(triggers.indexOf(trigger), 1),
      newTrigger: name => {
        const builder = {};
        for (const method of ['forSpreadsheet', 'onEdit', 'onChange', 'timeBased', 'everyMinutes']) builder[method] = () => builder;
        builder.create = () => triggers.push({getHandlerFunction: () => name});
        return builder;
      }
    },
    UrlFetchApp: {fetch: (url, options) => {
      const request = {url, ...options}; requests.push(request);
      const result = fetch ? fetch(request) : {status: 200, body: {id: 'new-gist', login: 'admin'}};
      return {getResponseCode: () => result.status, getContentText: () => JSON.stringify(result.body || {}), getAllHeaders: () => result.headers || {}};
    }}
  });
  vm.runInContext(script, context, {filename: 'SheetBoard.gs'});
  const originalReadRanges = context.readRanges_;
  const originalEnsureSettings = context.ensureSettingsSheet_;
  context.readRanges_ = () => ({ranges: {schedule: {rows: [['予定']]}}, warnings: []});
  context.ensureSettingsSheet_ = () => {};
  context.showMessage_ = (...args) => dialogs.push(args);
  return {context, users, shared, requests, mails, triggers, dialogs, lockWaits, spreadsheet, originalReadRanges, originalEnsureSettings, get releases() { return releases; }};
}

test('range references accept quoted, apostrophe-stripped and bare forms', () => {
  const {context: c} = harness();
  for (const text of ["予定表!A1:C10", "'予定表'!A1:C10", "予定表'!A1:C10"]) assert.deepEqual(plain(c.parseRangeRef(text)), {sheet: '予定表', a1: 'A1:C10'});
  assert.deepEqual(plain(c.parseRangeRef("'A''B!C'!A:F")), {sheet: "A'B!C", a1: 'A:F'});
  assert.deepEqual(plain(c.parseRangeRef(' A1:C10 ')), {sheet: '', a1: 'A1:C10'});
  assert.equal(c.parseRangeRef(''), null);
});

test('trim preserves background-only cells and supports legacy arguments', () => {
  const {context: c} = harness();
  assert.deepEqual(plain(c.trimEmpty([['x', ''], ['', ''], ['', '']], {bg: [['', ''], ['', '#FF0000'], ['', '']]})).values, [['x', ''], ['', '']]);
  assert.deepEqual(plain(c.trimEmpty([['x', ''], ['', '']], [['bold']], [['#FFF']])), {values: [['x']], weights: [['bold']], colors: [['#FFF']]});
});

test('formatting helpers handle colour, alignment, hidden cells and merges', () => {
  const {context: c} = harness();
  assert.equal(c.normalizeColor('#abc'), '#AABBCC');
  assert.equal(c.normalizeColor('#000000'), '');
  assert.equal(c.normalizeColor('invalid'), '');
  assert.equal(c.generalAlign('¥1,200'), 'r');
  assert.equal(c.generalAlign('予定'), 'l');
  assert.equal(c.mapAlign('center', '1'), 'c');
  assert.equal(c.styleBits('bold', 'italic', 'underline line-through'), 15);
  assert.deepEqual(plain(c.hiddenFilter([[1, 2], [3, 4]], [false, true], [true, false])), [[3]]);
  assert.deepEqual(plain(c.remapMerges([[0, 0, 3, 3], [1, 1, 1, 1]], [true, false, true], [true, true, false], 2, 2)), [[0, 0, 2, 2]]);
  assert.deepEqual(plain(c.parseRatioList('1, 2')), [1, 2]);
  assert.deepEqual(plain(c.parseRatioList('1,0')), []);
  assert.deepEqual(plain(c.parseAlignList('L,c,R')), ['l', 'c', 'r']);
  assert.deepEqual(plain(c.parseAlignList('l,x')), []);
});

test('payload keeps style layers, clips data and never emits bold', () => {
  const {context: c} = harness();
  const result = c.buildRangePayload([['x'.repeat(210), '']], [['bold', 'normal']], [['#000000', '#abc']], {format: true, extra: {styles: [[1, 0]], bg: [['', '#FF0000']], rowh: [21], colw: [100, 200]}});
  assert.equal(result.payload.rows[0][0].length, 200);
  assert.deepEqual(plain(result.payload.style), [[1, 0]]);
  assert.deepEqual(plain(result.payload.color), [['', '#AABBCC']]);
  assert.equal('bold' in result.payload, false);
  assert.equal(result.warnings.length, 1);
  const clipped = c.buildRangePayload(Array.from({length: 101}, () => Array(21).fill('x')), [], [], {});
  assert.equal(clipped.payload.rows.length, 100);
  assert.equal(clipped.payload.rows[0].length, 20);
});

test('stable hashing input and explicit timezone offsets', () => {
  const {context: c} = harness();
  assert.equal(c.stableStringify({b: {y: 2, x: 1}, a: [2, 1]}), '{"a":[2,1],"b":{"x":1,"y":2}}');
  assert.equal(c.isoWithOffset(new Date('2026-11-01T20:30:00Z'), 540), '2026-11-02T05:30:00+09:00');
  assert.equal(c.isoWithOffset(new Date('2026-11-02T02:00:00Z'), -300), '2026-11-01T21:00:00-05:00');
});

test('reordering configured ranges changes revision so an unnamed display follows the new first range', () => {
  const {context: c} = harness();
  const first = {schedule: {rows: [['予定']]}, notice: {rows: [['お知らせ']]}};
  const reordered = {notice: first.notice, schedule: first.schedule};
  assert.notEqual(c.computeRev_(first), c.computeRev_(reordered));
  assert.equal(c.computeRev_({schedule: {header: true, rows: [['予定']]}}), c.computeRev_({schedule: {rows: [['予定']], header: true}}));
});

test('tokenless callers only queue a publish and never alter errors or mail suppression', () => {
  const h = harness({user: {}, doc: {lastError: 'keep', lastMail_401: '123', gistOwner: 'admin'}});
  assert.equal(h.context.publish_(true, true), false);
  assert.deepEqual(h.shared.data, {lastError: 'keep', lastMail_401: '123', gistOwner: 'admin', pending: 'pending-1'});
  assert.deepEqual(h.lockWaits, [0]);
  assert.equal(h.releases, 1);
  assert.equal(h.requests.length + h.mails.length, 0);
});

test('staff without GitHub credentials see the shared world URL', () => {
  const h = harness({user: {}, doc: {worldUrl: 'https://gist.githubusercontent.com/admin/gist/raw/sheetboard.json'}});
  let html;
  h.context.showHtml_ = (_, value) => { html = value; };
  h.context.showStatus();
  assert.match(html, /https:\/\/gist\.githubusercontent\.com\/admin\/gist\/raw\/sheetboard\.json/);
  assert.equal(h.requests.length, 0);
});

test('setup writes a disabled, unquoted sample range', () => {
  const h = harness();
  let written;
  const sheet = {getMaxRows: () => 100, getRange: () => ({setNumberFormat() {}, insertCheckboxes() {}, setValues: value => { written = value; }})};
  h.spreadsheet.getSheetByName = () => null;
  h.spreadsheet.getSheets = () => [{getName: () => '公開前の予定'}];
  h.spreadsheet.insertSheet = () => sheet;
  h.originalEnsureSettings();
  assert.equal(written[1][0], '#sample');
  assert.equal(written[1][1], '公開前の予定!A1:F20');
});

test('commented sample rows are never read or included in the first publish', () => {
  const h = harness();
  h.spreadsheet.getSheetByName = () => ({getLastRow: () => 2, getRange: () => ({getValues: () => [['#sample', 'Secret!A1:F20', '', false, true, '', '']]})});
  h.spreadsheet.getSheets = () => { throw new Error('A disabled row must not read spreadsheet data'); };
  const result = h.originalReadRanges();
  assert.deepEqual(plain(result), {ranges: {}, warnings: []});
});

test('a busy trigger queues immediately without a request', () => {
  const h = harness({locked: true});
  assert.equal(h.context.publish_(false, true), false);
  assert.deepEqual(h.lockWaits, [0]);
  assert.equal(h.shared.data.pending, 'pending-1');
  assert.equal(h.requests.length, 0);
});

test('non-owners cannot install triggers during setup or reconnect, or update/recreate a missing gist', () => {
  const h = harness({user: {token: 'test-token', login: 'other'}, doc: {gistId: 'old', gistOwner: 'admin', worldUrl: 'keep'}});
  for (const action of [() => h.context.finishSetup(), () => h.context.ensureTriggers_(), () => h.context.publish_(true)]) assert.throws(action, /公開先を作り直す/);
  assert.equal(h.triggers.length, 0);
  assert.equal(h.requests.length, 0);
  assert.equal(h.shared.data.worldUrl, 'keep');
});

test('gist creation resolves missing login before building the world URL', () => {
  const h = harness({user: {token: 'test-token'}});
  assert.equal(h.context.publish_(true), true);
  assert.deepEqual(h.requests.map(r => r.method), ['get', 'post']);
  assert.equal(h.users.data.login, 'admin');
  assert.equal(h.shared.data.gistOwner, 'admin');
  assert.equal(h.shared.data.worldUrl, 'https://gist.githubusercontent.com/admin/new-gist/raw/sheetboard.json');
});

test('legacy owner is adopted and successful publish clears failure suppression', () => {
  const h = harness({doc: {gistId: 'old', lastError: 'failed', lastMail_401: '100', pending: 'queued'}});
  assert.equal(h.context.publish_(true), true);
  assert.equal(h.shared.data.gistOwner, 'admin');
  assert.equal(h.requests[0].method, 'patch');
  assert.equal(h.shared.data.lastError, '');
  assert.equal(h.shared.data.lastMail_401, undefined);
  assert.equal(h.shared.data.pending, undefined);
});

test('owner 404 recreates once, preserves URL-change suppression, and mails after expiry', () => {
  const h = harness({doc: {gistId: 'old', gistOwner: 'admin', lastMail_urlChange: String(Date.now())}, fetch: r => r.method === 'patch' ? {status: 404} : {status: 201, body: {id: 'new-gist'}}});
  const suppressedAt = h.shared.data.lastMail_urlChange;
  assert.equal(h.context.publish_(true), true);
  assert.deepEqual(h.requests.map(r => r.method), ['patch', 'post']);
  assert.equal(h.shared.data.lastMail_urlChange, suppressedAt);
  assert.equal(h.mails.length, 0);
  assert.match(h.shared.data.urlChangeNotice, /new-gist/);
  h.shared.setProperty('lastMail_urlChange', String(Date.now() - 25 * 3600000));
  assert.equal(h.context.publish_(true), true);
  assert.equal(h.mails.length, 1);
  assert.match(h.mails[0][2], /テストシート/);
  assert.match(h.mails[0][2], /https:\/\/example.test\/sheet/);
});

test('pending requests and hourly heartbeats publish unchanged content without changing updatedUnix', () => {
  const h = harness();
  h.context.publish_(true);
  const timestamp = h.shared.data.updatedUnix;
  h.requests.length = 0;
  assert.equal(h.context.publish_(false), true);
  assert.equal(h.requests.length, 0);
  h.shared.setProperty('pending', 'queued');
  h.context.onTimer();
  assert.equal(h.requests.length, 1);
  assert.equal(h.shared.data.pending, undefined);
  assert.equal(h.shared.data.updatedUnix, timestamp);
  h.shared.setProperty('lastPublishMillis', String(Date.now() - 61 * 60000));
  h.context.onTimer();
  assert.equal(h.requests.length, 2);
  assert.equal(h.shared.data.updatedUnix, timestamp);
});

test('confirmed administrator handoff creates a new destination and enables automatic publication', () => {
  const h = harness({user: {token: 'test-token', login: 'next-admin'}, doc: {gistId: 'old', gistOwner: 'old-admin', worldUrl: 'old-url'}});
  const result = h.context.recreateDestinationConfirmed();
  assert.equal(result.url, 'https://gist.githubusercontent.com/next-admin/new-gist/raw/sheetboard.json');
  assert.deepEqual(h.requests.map(r => r.method), ['post']);
  assert.deepEqual(h.triggers.map(t => t.getHandlerFunction()).sort(), ['onChangeInstalled', 'onEditInstalled', 'onTimer']);
});

test('handoff starts authorization when needed and never changes the destination before confirmation', () => {
  const h = harness({user: {}, doc: {gistId: 'old', gistOwner: 'admin', worldUrl: 'old-url'}});
  let authorization;
  h.context.beginAuthorization_ = setup => { authorization = setup; };
  h.context.recreateDestination();
  assert.equal(authorization, false);
  assert.deepEqual(h.shared.data, {gistId: 'old', gistOwner: 'admin', worldUrl: 'old-url'});
  assert.equal(h.requests.length + h.triggers.length, 0);
});

test('handoff installs no triggers when publication fails', () => {
  const h = harness({doc: {gistId: 'old', gistOwner: 'admin'}, fetch: () => ({status: 403})});
  assert.throws(() => h.context.recreateDestinationConfirmed(), /403/);
  assert.equal(h.triggers.length, 0);
});
