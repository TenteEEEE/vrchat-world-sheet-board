// Run from the repository root: node --test docs/Utsushi/tests/appsscript.test.cjs
// Apps Script and GitHub are mocked; these tests never publish or send mail.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const crypto = require('node:crypto');

const script = fs.readFileSync(path.resolve(__dirname, '../../../Packages/com.tentee.vrc-utsushi/AppsScript/Utsushi.gs'), 'utf8');
const plain = value => JSON.parse(JSON.stringify(value));

test('S2 settings sheet ID restores a renamed tab', () => {
  const h = harness({doc: {settingsSheetId: '42'}});
  let name = 'renamed';
  const sheet = {getSheetId: () => 42, getName: () => name, setName: value => {name = value;}};
  h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({getSheetByName: () => null, getSheets: () => [sheet]});
  assert.equal(h.context.findSettingsSheet_(), sheet);
  assert.equal(name, 'Utsushi設定');
});

test('S2 empty publication requires confirmation and trigger failure preserves Gist', () => {
  const h = harness({doc: {gistId: 'g', gistOwner: 'admin', publishedNameCount: '1'}});
  h.context.readRanges_ = () => ({ranges: {}, warnings: [], rowStatus: []});
  assert.deepEqual(plain(h.context.publish_(true, false, false, 'dialog')), {needsConfirm: 'empty'});
  assert.equal(h.requests.length, 0);
  assert.throws(() => h.context.publish_(false, true), /表示する範囲が1つもなくなった/);
  assert.equal(h.requests.length, 0);
  assert.equal(h.context.publish_(true, false, false, 'confirmed'), true);
  assert.equal(h.requests.filter(r => r.method === 'patch').length, 1);
});

test('S2 trigger ownership, disconnection and pending race', () => {
  const h = harness({doc: {gistId: 'g', gistOwner: 'admin'}});
  h.context.installTriggers_();
  assert.equal(h.shared.getProperty('triggerOwner'), crypto.createHash('sha256').update('admin@example.test').digest('hex').slice(0, 16));
  h.shared.setProperty('triggerOwner', 'another');
  h.context.onTimer();
  assert.equal(h.triggers.length, 0);
  h.shared.deleteProperty('triggerOwner');
  h.context.readRanges_ = () => {h.shared.setProperty('pending', 'new'); return {ranges: {a: {}}, warnings: [], rowStatus: []};};
  h.shared.setProperty('pending', 'old');
  assert.equal(h.context.publish_(true), true);
  assert.equal(h.shared.getProperty('pending'), 'new');
  h.context.disconnectConfirmed();
  assert.match(h.context.overallStatus_().text, /連携を解除/);
});

test('S2 setup guidance, copy fallback, version and fixed Gist description', () => {
  const h = harness({user: {}, doc: {}});
  h.context.publishNow();
  assert.match(h.dialogs.at(-1)[1], /まだセットアップされていません/);
  assert.match(h.context.clientHelpers_(), /execCommand\("copy"\)/);
  assert.match(h.context.clientHelpers_(), /Ctrl\+C/);
  h.context.showStatus();
  assert.match(h.dialogs.at(-1)[1], /スクリプトの版: 0\.6\.0/);
  const owner = harness();
  owner.context.createGist_('{}', 'test-token');
  assert.equal(JSON.parse(owner.requests[0].payload).description, 'Utsushi');
});

test('S5 setup status uses shared properties for staff', () => {
  const h = harness({user: {}, doc: {gistId: 'g'}});
  assert.equal(h.triggers.length, 0);
  h.context.publishNow();
  assert.match(h.dialogs.at(-1)[1], /受け付けました/);
  h.shared.setProperty('lastPublishOutcome', 'disconnected');
  h.context.publishNow();
  assert.equal(h.dialogs.at(-1)[1], '管理者がGitHub連携を解除したため、自動公開は止まっています。新しい管理者が Utsushi → セットアップ・修復 を実行してください。');
  h.shared.deleteProperty('gistId');
  h.context.publishNow();
  assert.match(h.dialogs.at(-1)[1], /まだセットアップされていません/);
});

test('S5 declining an empty publication reports cancellation', () => {
  const h = harness({doc: {gistId: 'g', gistOwner: 'admin', publishedNameCount: '1', lastPublishOutcome: 'failure'}});
  h.context.readRanges_ = () => ({ranges: {}, warnings: [], rowStatus: []});
  h.context.SpreadsheetApp.getUi = () => ({ButtonSet: {YES_NO: 'yes_no'}, Button: {YES: 'yes'}, alert: () => 'no'});
  h.context.publishNow();
  assert.equal(h.dialogs.at(-1)[1], '公開を取りやめました。掲示板は変わっていません。');
  assert.equal(h.requests.length, 0);
});

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

function harness({user = {token: 'test-token', login: 'admin'}, doc = {}, fetch, locked = false, userLocked = false, clientId = 'Ov23liVU3bFAK2lWtKPk'} = {}) {
  const users = properties(user), shared = properties(doc);
  const requests = [], mails = [], triggers = [], dialogs = [], lockWaits = [], userLockWaits = [];
  let releases = 0, uuid = 0;
  let userLockHeld = userLocked;
  const spreadsheet = {getName: () => 'テストシート', getUrl: () => 'https://example.test/sheet', getSpreadsheetTimeZone: () => 'Asia/Tokyo', getSheetByName: () => null, getActiveSheet: () => null, getActiveRange: () => null};
  const context = vm.createContext({
    PropertiesService: {getUserProperties: () => users, getDocumentProperties: () => shared},
    LockService: {
      getDocumentLock: () => ({tryLock: ms => { lockWaits.push(ms); return !locked; }, releaseLock: () => releases++}),
      getUserLock: () => ({
        tryLock: ms => { userLockWaits.push(ms); if (userLockHeld) return false; userLockHeld = true; return true; },
        releaseLock: () => { userLockHeld = false; releases++; }
      })
    },
    Utilities: {
      getUuid: () => 'pending-' + ++uuid,
      DigestAlgorithm: {MD5: 'md5', SHA_256: 'sha256'}, Charset: {UTF_8: 'utf8'},
      computeDigest: (algorithm, text) => Array.from(crypto.createHash(algorithm).update(text, 'utf8').digest()),
      newBlob: text => ({getBytes: () => Buffer.from(text, 'utf8')}),
      formatDate: (date, zone, pattern) => { if (pattern === 'Z') return '+0900'; const d = new Date(date); const parts = new Intl.DateTimeFormat('en-CA', {timeZone: zone || 'Asia/Tokyo', year: 'numeric', month: '2-digit', day: '2-digit', hour: '2-digit', minute: '2-digit', hourCycle: 'h23'}).formatToParts(d).reduce((a, x) => (a[x.type] = x.value, a), {}); return parts.year + '/' + parts.month + '/' + parts.day + ' ' + parts.hour + ':' + parts.minute; }
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
      return {getResponseCode: () => result.status, getContentText: () => result.text === undefined ? JSON.stringify(result.body || {}) : result.text, getAllHeaders: () => result.headers || {}};
    }}
  });
  const source = script.replace(/const GITHUB_CLIENT_ID = '[^']*'/, "const GITHUB_CLIENT_ID = '" + clientId + "'")
    .replaceAll('sheet.getSheetId()', '(sheet.getSheetId ? sheet.getSheetId() : 1)');
  vm.runInContext(source, context, {filename: 'Utsushi.gs'});
  context.__readRanges_ = context.readRanges_;
  context.__ensureSettingsSheet_ = context.ensureSettingsSheet_;
  context.readRanges_ = () => {
    const ranges = {schedule: {rows: [['予定']]}};
    return {ranges, warnings: []};
  };
  context.ensureSettingsSheet_ = () => {};
  context.showMessage_ = (...args) => dialogs.push(args);
  context.showHtml_ = (title, html, options) => dialogs.push([title, context.page_(html, options), options]);
  return {context, users, shared, requests, mails, triggers, dialogs, lockWaits, userLockWaits, get releases() { return releases; }};
}

function configureReadableSheet(h, config, {rows = 1, cols = 1, filter = false, hiddenRows = new Set()} = {}) {
  let filterCalls = 0;
  const cell = {
    getRow: () => 1, getColumn: () => 1, getNumRows: () => rows, getNumColumns: () => cols,
    getDisplayValues: () => Array.from({length: rows}, (_, r) => Array.from({length: cols}, (_, c) => `r${r + 1}c${c + 1}`)),
    getDataValidations: () => Array.from({length: rows}, () => Array(cols).fill(null)),
    getMergedRanges: () => []
  };
  const source = {
    getName: () => 'Source', getLastRow: () => rows, getLastColumn: () => cols,
    getRange: () => cell, getFilter: () => { filterCalls++; return filter ? {} : null; },
    isRowHiddenByUser: () => false, isRowHiddenByFilter: row => hiddenRows.has(row),
    isColumnHiddenByUser: () => false
  };
  const settings = {
    getLastRow: () => config.length + 1,
    getRange: () => ({getValues: () => config})
  };
  h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({
    getSheetByName: name => name === 'Utsushi設定' ? settings : name === 'Source' ? source : null,
    getSheets: () => [source], getUrl: () => 'https://example.test/sheet'
  });
  h.context.readRanges_ = h.context.__readRanges_;
  return {getFilterCalls: () => filterCalls};
}

test('range references accept quoted, apostrophe-stripped and bare forms', () => {
  const {context: c} = harness();
  for (const text of ["予定表!A1:C10", "'予定表'!A1:C10", "予定表'!A1:C10"]) assert.deepEqual(plain(c.parseRangeRef(text)), {sheet: '予定表', a1: 'A1:C10'});
  assert.deepEqual(plain(c.parseRangeRef("'A''B!C'!A:F")), {sheet: "A'B!C", a1: 'A:F'});
  assert.deepEqual(plain(c.parseRangeRef(' A1:C10 ')), {sheet: '', a1: 'A1:C10'});
  assert.equal(c.parseRangeRef(''), null);
});

test('trim preserves background-only cells without legacy arguments', () => {
  const {context: c} = harness();
  assert.deepEqual(plain(c.trimEmpty([['x', ''], ['', ''], ['', '']], {bg: [['', ''], ['', '#FF0000'], ['', '']]})).values, [['x', ''], ['', '']]);
  assert.deepEqual(plain(c.trimEmpty([['x', ''], ['', '']], {styles: [[1]], bg: [['#FFF']]})), {values: [['x']], layers: {styles: [[1]], bg: [['#FFF']]}});
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

test('chipSwatch matches trimmed display values and normalizes swatch colours', () => {
  const {context: c} = harness();
  assert.deepEqual(plain(c.chipSwatch([['Empty', 'Dark']], [['#FFFFFF', '#B10202']], [['#000000', '#FFFFFF']], 'Empty')),
    {bg: '', fg: ''});
  assert.deepEqual(plain(c.chipSwatch([['Dark']], [['#b10202']], [['#990000']], ' Dark ')),
    {bg: '#B10202', fg: '#990000'});
  assert.equal(c.chipSwatch([['one']], [['#FF0000']], [['#000000']], 'two'), null);
  assert.equal(c.chipSwatch(undefined, undefined, undefined, 'x'), null);
});

test('chipCode encodes hidden dropdowns, empty values, defaults, and partial swatches', () => {
  const {context: c} = harness();
  assert.equal(c.chipCode('x', false, null), '');
  assert.equal(c.chipCode('', true, null), 'a');
  assert.equal(c.chipCode('x', true, null), 'c');
  assert.equal(c.chipCode('x', true, {bg: '#B10202', fg: '#FFFFFF'}), 'c|#B10202|#FFFFFF');
  assert.equal(c.chipCode('x', true, {bg: '', fg: '#990000'}), 'c||#990000');
  assert.equal(c.chipCode('x', true, {bg: '#FFE599', fg: ''}), 'c|#FFE599|');
});

test('payload preserves large rows, columns, and cell text without clipping', () => {
  const {context: c} = harness();
  const result = c.buildRangePayload([['x'.repeat(1000), '']], [['#000000', '#abc']], {format: true, extra: {styles: [[1, 0]], bg: [['', '#FF0000']], rowh: [21], colw: [100, 200]}});
  assert.equal(result.payload.rows[0][0].length, 1000);
  assert.deepEqual(plain(result.payload.style), [[1, 0]]);
  assert.deepEqual(plain(result.payload.color), [['', '#AABBCC']]);
  assert.equal('bold' in result.payload, false);
  assert.deepEqual(plain(result.warnings), []);
  const large = c.buildRangePayload(Array.from({length: 150}, () => Array(30).fill('x')), [], {});
  assert.equal(large.payload.rows.length, 150);
  assert.equal(large.payload.rows[0].length, 30);
});

test('payload omits default formatting layers and keeps a layer when one cell differs', () => {
  const {context: c} = harness();
  const defaults = c.buildRangePayload([['x', 'y']], [], {format: true, extra: {
    styles: [[0, 0]], sizes: [[12, 12]], bg: [['', '']], ha: [['l', 'l']], wrap: [[0, 0]]
  }}).payload;
  for (const key of ['style', 'bg', 'color', 'ha', 'wrap']) assert.equal(key in defaults, false);
  assert.deepEqual(plain(defaults.size), [[12, 12]]);
  const changed = c.buildRangePayload([['x', 'y']], [['', '#00FF00']], {format: true, extra: {
    styles: [[0, 1]], bg: [['', '#FF0000']], ha: [['l', 'r']], wrap: [[0, 1]]
  }}).payload;
  for (const key of ['style', 'bg', 'ha', 'wrap', 'color']) assert.equal(key in changed, true);
});

test('payload emits chip layers, omits empty layers, and trims chip-only edges', () => {
  const {context: c} = harness();
  const populated = c.buildRangePayload([['x', 'y']], [], {extra: {chips: [['c', 'c||#990000']]}}).payload;
  assert.deepEqual(plain(populated.chip), [['c', 'c||#990000']]);
  const empty = c.buildRangePayload([['x']], [], {extra: {chips: [['']]}}).payload;
  assert.equal('chip' in empty, false);
  const clipped = c.buildRangePayload([['x', ''], ['', '']], [], {extra: {chips: [[''], ['', 'c']]}}).payload;
  assert.deepEqual(plain(clipped.rows), [['x']]);
  assert.equal('chip' in clipped, false);
});

test('status arrays fit within the document property limit by UTF-8 bytes', () => {
  const {context: c} = harness();
  const input = Array(100).fill('日'.repeat(120));
  input[input.length - 1] = '';
  const values = c.boundedStatusArray_(input);
  assert.ok(values.every(value => value.length <= 120));
  assert.ok(Buffer.byteLength(JSON.stringify(plain(values)), 'utf8') <= 8000);
});

test('readRanges_ publishes 150 rows and 30 columns without applying former caps', () => {
  const h = harness();
  configureReadableSheet(h, [['wide', 'Source!A1:AD150']], {rows: 150, cols: 30});
  const range = h.context.readRanges_().ranges.wide;
  assert.equal(range.rows.length, 150);
  assert.equal(range.rows[0].length, 30);
});

test('readRanges_ publishes 25 configured ranges', () => {
  const h = harness();
  const config = Array.from({length: 25}, (_, index) => [`range${index + 1}`, 'Source!A1']);
  configureReadableSheet(h, config);
  assert.equal(Object.keys(plain(h.context.readRanges_().ranges)).length, 25);
});

test('readRanges_ warns on a large visible range without rejecting it', () => {
  const h = harness();
  configureReadableSheet(h, [['large', 'Source!A1:BS71']], {rows: 71, cols: 71});
  const result = h.context.readRanges_();
  assert.equal(result.ranges.large.rows.length, 71);
  assert.match(result.rowStatus[0], /範囲が大きいため、公開に時間がかかり、掲示板の文字がとても小さくなります/);
});

test('readRanges_ keeps only the 40 visible rows in a 300-row filtered range', () => {
  const h = harness();
  const hiddenRows = new Set(Array.from({length: 300}, (_, index) => index + 1).filter(row => row < 201 || row > 240));
  const sheet = configureReadableSheet(h, [['filtered', 'Source!A1:A300']], {rows: 300, filter: true, hiddenRows});
  const range = h.context.readRanges_().ranges.filtered;
  assert.equal(range.rows.length, 40);
  assert.equal(range.rows[0][0], 'r201c1');
  assert.equal(range.rows.at(-1)[0], 'r240c1');
  assert.equal(sheet.getFilterCalls(), 1);
});

test('readRanges_ does not call isRowHiddenByFilter when the sheet has no filter', () => {
  const h = harness();
  let filterRowCalls = 0;
  const sheet = configureReadableSheet(h, [['plain', 'Source!A1']], {rows: 1});
  const source = h.context.SpreadsheetApp.getActiveSpreadsheet().getSheetByName('Source');
  source.isRowHiddenByFilter = () => { filterRowCalls++; return false; };
  h.context.readRanges_();
  assert.equal(sheet.getFilterCalls(), 1);
  assert.equal(filterRowCalls, 0);
});

test('publish_ stops before PATCH when its execution budget is exceeded', () => {
  const h = harness({doc: {gistId: 'existing', gistOwner: 'admin', worldUrl: 'https://example.test/world'}});
  configureReadableSheet(h, [['range', 'Source!A1']]);
  let notified = false;
  const notifyError = h.context.notifyError_;
  h.context.notifyError_ = (...args) => { notified = true; return notifyError(...args); };
  let clockCalls = 0;
  h.context.nowMillis_ = () => clockCalls++ === 0 ? 1000 : 271001;
  assert.throws(() => h.context.publish_(true), /表が大きすぎて、時間内に読み切れませんでした/);
  assert.equal(h.requests.filter(request => request.method === 'patch').length, 0);
  assert.equal(h.shared.data.lastPublishStatus, 'failure');
  assert.match(h.shared.data.lastError, /エラー番号 408/);
  assert.equal(notified, true);
});

test('publish_ rejects JSON over 10 MB before PATCH', () => {
  const h = harness({doc: {gistId: 'existing', gistOwner: 'admin', worldUrl: 'https://example.test/world'}});
  h.context.readRanges_ = () => ({ranges: {large: {rows: [['x'.repeat(10 * 1024 * 1024)]]}}, warnings: [], rowStatus: []});
  assert.throws(() => h.context.publish_(true), /掲示板に出す内容が 10MB を超えたため公開できません/);
  assert.equal(h.requests.filter(request => request.method === 'patch').length, 0);
  assert.equal(h.shared.data.lastPublishStatus, 'failure');
});

test('publish_ keeps the iPhone warning when a large unchanged payload skips PATCH', () => {
  const h = harness({doc: {gistId: 'existing', gistOwner: 'admin', worldUrl: 'https://example.test/world'}});
  h.context.readRanges_ = () => ({ranges: {large: {rows: [['x'.repeat(820 * 1024)]]}}, warnings: [], rowStatus: []});
  assert.equal(h.context.publish_(true), true);
  assert.equal(h.requests.filter(request => request.method === 'patch').length, 1);
  assert.match(h.shared.data.warnings, /掲示板の内容が大きいため、iPhone 版 VRChat では表示できないことがあります/);
  assert.ok(Number(h.shared.data.lastContentBytes) > 800 * 1024);
  h.shared.data.lastPublishMillis = String(Date.now());
  assert.equal(h.context.publish_(false), true);
  assert.equal(h.requests.filter(request => request.method === 'patch').length, 1);
  assert.match(h.shared.data.warnings, /掲示板の内容が大きいため、iPhone 版 VRChat では表示できないことがあります/);
});

test('publish_ keeps Japanese rowStatus within 8000 UTF-8 bytes', () => {
  const h = harness();
  h.context.readRanges_ = () => ({ranges: {one: {rows: [['x']]}}, warnings: [], rowStatus: Array(100).fill('注意'.repeat(60))});
  assert.equal(h.context.publish_(true), true);
  assert.ok(Buffer.byteLength(h.shared.data.rowStatus, 'utf8') <= 8000);
});

test('publish_ publishes when publishedNames exceeds 8000 bytes and reports omitted names neutrally', () => {
  const h = harness();
  const ranges = Object.fromEntries(Array.from({length: 1200}, (_, index) => [`range${index}`, {rows: [['x']]}]));
  h.context.readRanges_ = () => ({ranges, warnings: [], rowStatus: []});
  assert.equal(h.context.publish_(true), true);
  assert.ok(Buffer.byteLength(h.shared.data.publishedNames, 'utf8') <= 8000);
  assert.equal(h.shared.data.publishedNamesIncomplete, 'true');
  assert.equal(Number(h.shared.data.publishedNameCount), 1200);
  assert.doesNotMatch(h.context.overallStatus_().text, /掲示板に出す範囲がありません/);
  h.context.showStatus();
  assert.match(h.dialogs.at(-1)[1], /ほか \d+ 件/);
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
  assert.deepEqual(h.shared.data, {lastError: 'keep', lastMail_401: '123', gistOwner: 'admin', pending: 'pending-1', lastPublishOutcome: 'no_token'});
  assert.deepEqual(h.lockWaits, [0]);
  assert.equal(h.releases, 1);
  assert.equal(h.requests.length + h.mails.length, 0);
});

test('a busy trigger queues immediately without a request', () => {
  const h = harness({locked: true});
  assert.equal(h.context.publish_(false, true), false);
  assert.deepEqual(h.lockWaits, [0]);
  assert.equal(h.shared.data.pending, 'pending-1');
  assert.equal(h.requests.length, 0);
  assert.equal(h.context.publishFailureMessage_(), 'ほかの公開処理が進行中です。このあと自動で公開されるので、操作は不要です。');
});

test('non-owners cannot install triggers during setup or reconnect, or update/recreate a missing gist', () => {
  const h = harness({user: {token: 'test-token', login: 'other'}, doc: {gistId: 'old', gistOwner: 'admin', worldUrl: 'keep'}});
  for (const action of [() => h.context.finishSetup(), () => h.context.ensureTriggers_()]) assert.throws(action, /公開先を作り直す/);
  assert.throws(() => h.context.publish_(true), /公開先を作り直す/);
  assert.equal(h.triggers.length, 0);
  assert.equal(h.requests.length, 0);
  assert.equal(h.shared.data.worldUrl, 'keep');
});

test('Device Flow hands off an existing destination without installing triggers', () => {
  const h = harness({doc: {gistId: 'existing', gistOwner: 'admin', worldUrl: 'keep'}, fetch: request => {
    if (request.url.endsWith('/login/device/code')) return {status: 200, body: {device_code: 'device-code', user_code: 'ABCD-EFGH', verification_uri: 'https://github.com/login/device', interval: 5}};
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'staff-token'}};
    if (request.url === 'https://api.github.com/user') return {status: 200, body: {login: 'staff'}};
    return {status: 200, body: {}};
  }});
  h.context.beginAuthorization_(true);
  const result = h.context.pollDeviceToken();
  assert.deepEqual(plain(result), {status: 'success', handoff: true});
  assert.equal(h.users.data.token, 'staff-token');
  assert.equal(h.triggers.length, 0);
  assert.match(h.dialogs.at(-1)[1], /r\.handoff\?/);
  assert.match(h.dialogs.at(-1)[1], /連携しました。メニュー「Utsushi → 管理者用 → 公開先を作り直す」を実行してください。/);
});

test('gist creation resolves missing login before building the world URL', () => {
  const h = harness({user: {token: 'test-token'}});
  assert.equal(h.context.publish_(true), true);
  assert.deepEqual(h.requests.map(r => r.method), ['get', 'post']);
  assert.equal(h.users.data.login, 'admin');
  assert.equal(h.shared.data.gistOwner, 'admin');
  assert.equal(h.shared.data.worldUrl, 'https://gist.githubusercontent.com/admin/new-gist/raw/sheetboard.json');
});

test('first setup creates a URL even when the only configured range is the sample placeholder', () => {
  const h = harness();
  h.context.readRanges_ = () => ({ranges: {}, warnings: []});
  assert.equal(h.context.publish_(true), true);
  assert.deepEqual(h.requests.map(r => r.method), ['post']);
  assert.equal(h.shared.data.worldUrl, 'https://gist.githubusercontent.com/admin/new-gist/raw/sheetboard.json');
  const gistRequest = JSON.parse(h.requests[0].payload);
  const document = JSON.parse(gistRequest.files['sheetboard.json'].content);
  assert.deepEqual(plain(document.ranges), {});
  assert.deepEqual(plain(document.order), []);
  assert.equal(document.format, 'sheetboard/1');
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

test('a failed PATCH leaves the previously published Gist document intact', () => {
  let remote = {ranges: {old: {rows: [['old']]} }};
  let attempted;
  const h = harness({user: {token: 'test-token', login: 'admin'}, doc: {gistId: 'existing', gistOwner: 'admin', worldUrl: 'keep', lastContentBytes: '123'}, fetch: request => {
    if (request.url.endsWith('/gists/existing') && request.method === 'patch') {
      attempted = JSON.parse(request.payload).files['sheetboard.json'].content;
      return {status: 500, body: {message: 'failed'}};
    }
    return {status: 200, body: {}};
  }});
  h.context.readRanges_ = () => ({ranges: {new: {rows: [['new']]}}, warnings: [], rowStatus: []});
  h.context.notifyError_ = () => {};
  h.context.writeSheetStatus_ = () => {};
  const original = JSON.stringify(remote);
  assert.throws(() => h.context.publish_(true), /GitHubへの公開に失敗しました/);
  assert.notEqual(attempted, original);
  assert.equal(JSON.stringify(remote), original);
  assert.equal(h.shared.data.lastContentBytes, '123');
});

test('old administrator timer stops after handoff without writing shared state or sending mail', () => {
  const h = harness({user: {token: 'old-token', login: 'old-admin'}, doc: {
    gistId: 'existing', gistOwner: 'new-admin', worldUrl: 'keep', pending: 'queued', rowStatus: '["keep"]', warnings: '["keep"]',
    lastError: 'keep', lastPublishStatus: 'failure', lastPublishOutcome: 'failure'
  }});
  h.triggers.push({getHandlerFunction: () => 'onTimer'});
  h.context.readRanges_ = () => { throw new Error('must not read ranges after handoff'); };
  const before = JSON.stringify(h.shared.data);
  assert.equal(h.context.publish_(false, true), false);
  assert.equal(JSON.stringify(h.shared.data), before);
  assert.equal(h.triggers.length, 0);
  assert.equal(h.mails.length, 0);
});

test('old administrator manual publish reports owner instructions without changing shared state', () => {
  const h = harness({user: {token: 'old-token', login: 'old-admin'}, doc: {gistId: 'existing', gistOwner: 'new-admin', worldUrl: 'keep', lastError: 'keep'}});
  const before = JSON.stringify(h.shared.data);
  h.context.publishNow();
  assert.match(h.dialogs.at(-1)[1], /この公開先は new-admin さんのGitHubにあります/);
  assert.equal(JSON.stringify(h.shared.data), before);
  assert.equal(h.mails.length, 0);
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
  assert.equal(authorization, undefined);
  assert.match(h.dialogs.at(-1)[1], /更新されなくなります/);
  assert.deepEqual(h.shared.data, {gistId: 'old', gistOwner: 'admin', worldUrl: 'old-url'});
  assert.equal(h.requests.length + h.triggers.length, 0);
});

test('handoff installs no triggers when publication fails', () => {
  const h = harness({doc: {gistId: 'old', gistOwner: 'admin'}, fetch: () => ({status: 403})});
  assert.throws(() => h.context.recreateDestinationConfirmed(), /GitHubで公開する許可がありません/);
  assert.equal(h.triggers.length, 0);
});

test('Device Flow stores the complete expiring token set without a client secret', () => {
  const h = harness({user: {token: 'old-token', refreshToken: 'old-refresh', tokenExpiresAt: '1', refreshTokenExpiresAt: '2', reauthRequired: 'true'}, fetch: request => {
    if (request.url.endsWith('/login/device/code')) return {status: 200, body: {device_code: 'device-secret', user_code: 'ABCD-EFGH', verification_uri: 'https://github.com/login/device', interval: 5}};
    if (request.url.endsWith('/login/oauth/access_token') && request.payload.device_code) return {status: 200, body: {access_token: 'new-access-secret', refresh_token: 'new-refresh-secret', expires_in: 28800, refresh_token_expires_in: 15552000}};
    if (request.url === 'https://api.github.com/user') return {status: 200, body: {login: 'admin'}};
    return {status: 200, body: {}};
  }});
  h.context.beginAuthorization_(false);
  const result = h.context.pollDeviceToken();
  assert.equal(result.status, 'success');
  assert.equal(h.users.data.token, 'new-access-secret');
  assert.equal(h.users.data.refreshToken, 'new-refresh-secret');
  assert.equal(h.users.data.authType, 'oauth');
  assert.ok(Number(h.users.data.tokenExpiresAt) > Date.now());
  assert.ok(Number(h.users.data.refreshTokenExpiresAt) > Number(h.users.data.tokenExpiresAt));
  assert.equal(h.users.data.deviceCode, undefined);
  assert.equal(h.users.data.reauthRequired, undefined);
  const tokenExchange = h.requests.find(request => request.url.endsWith('/login/oauth/access_token'));
  assert.equal(tokenExchange.payload.grant_type, 'urn:ietf:params:oauth:grant-type:device_code');
  assert.equal('client_secret' in tokenExchange.payload, false);
  assert.equal(JSON.stringify(h.dialogs).includes('new-access-secret'), false);
});

test('refreshes near expiry and persists the rotated tokens before later API calls', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 4 * 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000), login: 'admin'}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) {
      assert.deepEqual(plain(request.payload), {client_id: 'Ov23liVU3bFAK2lWtKPk', grant_type: 'refresh_token', refresh_token: 'refresh-a'});
      return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    }
    return {status: 200, body: {id: 'ok'}};
  }});
  assert.equal(h.context.refreshAccessToken_(false), 'access-b');
  assert.equal(h.users.data.token, 'access-b');
  assert.equal(h.users.data.refreshToken, 'refresh-b');
  assert.ok(Number(h.users.data.tokenExpiresAt) > Date.now() + 7 * 3600000);
  assert.ok(Number(h.users.data.refreshTokenExpiresAt) > Number(h.users.data.tokenExpiresAt));
  assert.equal(h.requests[0].url, 'https://github.com/login/oauth/access_token');
});

test('the five-minute timer refreshes an unchanged publication before its token expires', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 4 * 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000), login: 'admin'}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    throw new Error('Unchanged content should not call GitHub API');
  }});
  const ranges = h.context.readRanges_().ranges;
  const revision = h.context.computeRev_(ranges);
  h.shared.setProperties({gistId: 'existing', gistOwner: 'admin', lastRev: revision, lastPublishMillis: String(Date.now()), updatedUnix: '123'});
  h.context.onTimer();
  assert.equal(h.requests.length, 1);
  assert.ok(h.requests[0].url.endsWith('/login/oauth/access_token'));
  assert.equal(h.users.data.token, 'access-b');
  assert.equal(h.shared.data.updatedUnix, '123');
});

test('a transient refresh failure queues an unchanged publication for recovery on the next timer', () => {
  let refreshAttempt = 0;
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000), login: 'admin'}, doc: {gistId: 'existing', gistOwner: 'admin'}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) {
      refreshAttempt++;
      if (refreshAttempt === 1) return {status: 503, body: {error: 'temporarily_unavailable'}};
      return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    }
    return {status: 200, body: {id: 'existing'}};
  }});
  const revision = h.context.computeRev_(h.context.readRanges_().ranges);
  h.shared.setProperties({lastRev: revision, lastPublishMillis: String(Date.now()), lastError: 'old error', lastPublishStatus: 'failure'});
  h.context.onTimer();
  assert.equal(h.shared.data.pending, 'pending-1');
  assert.match(h.shared.data.lastError, /GitHub連携を更新できませんでした。自動で再試行します。操作は不要です（エラー番号 503）。/);
  h.context.onTimer();
  assert.equal(refreshAttempt, 2);
  assert.equal(h.users.data.token, 'access-b');
  assert.equal(h.shared.data.pending, undefined);
  assert.equal(h.shared.data.lastError, '');
  assert.equal(h.shared.data.lastPublishStatus, 'success');
});

test('tokens outside the refresh margin do not trigger a refresh', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 6 * 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}});
  assert.equal(h.context.refreshAccessToken_(false), 'access-a');
  assert.equal(h.requests.length, 0);
});

test('simultaneous timer-style executions reuse the first rotated token', () => {
  let refreshCalls = 0;
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) {
      refreshCalls++;
      return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    }
    return {status: 200, body: {}};
  }});
  assert.equal(h.context.refreshAccessToken_(false), 'access-b');
  assert.equal(h.context.refreshAccessToken_(false), 'access-b');
  assert.equal(h.context.refreshAccessToken_(true, 'access-a'), 'access-b');
  assert.equal(refreshCalls, 1);
});

test('a GitHub API 401 refreshes and retries the API request no more than once', () => {
  let apiCalls = 0;
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 3600000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    apiCalls++;
    return {status: 401, body: {message: 'unauthorized'}};
  }});
  assert.throws(() => h.context.githubRequest_('patch', '/gists/example', {files: {} }), /セットアップ・修復/);
  assert.equal(apiCalls, 2);
  assert.equal(h.users.data.reauthRequired, 'true');
});

test('a 401 retry uses the newly rotated access token in its Authorization header', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 3600000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'access-b', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    return {status: request.headers.Authorization === 'Bearer access-a' ? 401 : 200, body: {id: 'ok'}};
  }});
  assert.equal(h.context.githubRequest_('patch', '/gists/example', {files: {}}).body.id, 'ok');
  const api = h.requests.filter(request => request.url === 'https://api.github.com/gists/example');
  assert.deepEqual(api.map(request => request.headers.Authorization), ['Bearer access-a', 'Bearer access-b']);
});

test('an expired access token is refreshed before an ordinary API request', () => {
  const h = harness({user: {token: 'expired-access', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() - 1000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'fresh-access', refresh_token: 'refresh-b', expires_in: 28800, refresh_token_expires_in: 15552000}};
    return {status: request.headers.Authorization === 'Bearer fresh-access' ? 200 : 401, body: {id: 'ok'}};
  }});
  assert.equal(h.context.githubRequest_('get', '/user').body.id, 'ok');
  assert.equal(h.requests.filter(request => request.url.endsWith('/login/oauth/access_token')).length, 1);
  assert.equal(h.requests.filter(request => request.url === 'https://api.github.com/user').length, 1);
});

test('temporary refresh failures preserve both credentials and can be retried later', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: () => ({status: 503, body: {error: 'temporarily_unavailable'}})});
  assert.throws(() => h.context.refreshAccessToken_(false), /GitHub連携を更新できませんでした。自動で再試行します。操作は不要です/);
  assert.equal(h.users.data.token, 'access-a');
  assert.equal(h.users.data.refreshToken, 'refresh-a');
  assert.equal(h.users.data.reauthRequired, undefined);
});

test('network throws, rate limits, permission errors, and invalid JSON preserve refresh credentials', () => {
  const cases = [
    {name: 'network exception', fetch: () => { throw new Error('https://secret.invalid/?refresh_token=leak'); }},
    {name: 'rate limit', fetch: () => ({status: 429, body: {message: 'slow down'}})},
    {name: 'permission/config error', fetch: () => ({status: 403, body: {error: 'unauthorized_client'}})},
    {name: 'malformed JSON', fetch: () => ({status: 200, text: '{bad json'})}
  ];
  for (const testCase of cases) {
    const h = harness({user: {token: 'access-a', refreshToken: 'refresh-secret', tokenExpiresAt: String(Date.now() + 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: testCase.fetch});
    assert.throws(() => h.context.refreshAccessToken_(false));
    assert.equal(h.users.data.token, 'access-a', testCase.name);
    assert.equal(h.users.data.refreshToken, 'refresh-secret', testCase.name);
    assert.equal(h.users.data.reauthRequired, undefined, testCase.name);
    assert.equal(h.requests.length, 1, testCase.name);
    assert.equal(JSON.stringify(h.dialogs).includes('refresh-secret'), false, testCase.name);
    assert.equal(JSON.stringify(h.mails).includes('refresh-secret'), false, testCase.name);
  }
});

test('invalid or expired refresh credentials retain values and require reauthorization', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 60000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000)}, fetch: () => ({status: 200, body: {error: 'bad_refresh_token'}})});
  assert.throws(() => h.context.refreshAccessToken_(false), /GitHub連携の期限が切れたため、公開が止まっています/);
  assert.equal(h.users.data.token, 'access-a');
  assert.equal(h.users.data.refreshToken, 'refresh-a');
  assert.equal(h.users.data.reauthRequired, 'true');
});

test('a busy user lock does not issue a second refresh request or discard credentials', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() + 60000)}, userLocked: true});
  assert.throws(() => h.context.refreshAccessToken_(false), /GitHub連携を更新できませんでした。自動で再試行します。操作は不要です/);
  assert.equal(h.requests.length, 0);
  assert.equal(h.users.data.token, 'access-a');
  assert.equal(h.users.data.refreshToken, 'refresh-a');
});

test('legacy non-expiring tokens remain usable, while PAT reconnection clears OAuth metadata', () => {
  const h = harness({user: {token: 'old-pat', login: 'admin', refreshToken: 'old-refresh', tokenExpiresAt: '123', refreshTokenExpiresAt: '456', authType: 'oauth', reauthRequired: 'true'}, fetch: request => {
    if (request.url === 'https://api.github.com/user') return {status: 200, body: {login: 'admin'}, headers: {'X-OAuth-Scopes': 'gist'}};
    return {status: 200, body: {id: 'ok'}};
  }});
  assert.deepEqual(plain(h.context.saveToken('new-pat', true)), {handoff: false});
  assert.equal(h.users.data.token, 'new-pat');
  for (const key of ['refreshToken', 'tokenExpiresAt', 'refreshTokenExpiresAt', 'authType', 'reauthRequired', 'deviceCode']) assert.equal(h.users.data[key], undefined);
  h.requests.length = 0;
  assert.deepEqual(plain(h.context.githubRequest_('get', '/user')), {status: 200, body: {login: 'admin'}, scopes: 'gist'});
  assert.deepEqual(plain(h.context.githubRequest_('get', '/user')), {status: 200, body: {login: 'admin'}, scopes: 'gist'});
  assert.equal(h.requests.some(request => request.url.endsWith('/login/oauth/access_token')), false);
});

test('failed explicit-token validation preserves the currently saved authorization', () => {
  const h = harness({user: {token: 'current-access', refreshToken: 'current-refresh', tokenExpiresAt: '123', refreshTokenExpiresAt: '456', authType: 'oauth'}, fetch: () => ({status: 401, body: {message: 'invalid'}})});
  assert.throws(() => h.context.saveToken('candidate-token', true), /401/);
  assert.equal(h.users.data.token, 'current-access');
  assert.equal(h.users.data.refreshToken, 'current-refresh');
  assert.equal(h.users.data.reauthRequired, undefined);
});

test('a stale 401 cannot mark a newly connected token as requiring reauthorization', () => {
  const h = harness({user: {token: 'new-access', refreshToken: 'new-refresh', tokenExpiresAt: String(Date.now() + 3600000)}, fetch: () => ({status: 401, body: {}})});
  assert.equal(h.context.markReauthRequired_('old-access'), false);
  assert.equal(h.users.data.reauthRequired, undefined);
});

test('disconnect removes all Utsushi credentials but preserves unrelated UserProperties', () => {
  const h = harness({user: {token: 'access-a', login: 'admin', deviceCode: 'device-a', authType: 'oauth', refreshToken: 'refresh-a', tokenExpiresAt: '123', refreshTokenExpiresAt: '456', reauthRequired: 'true', unrelated: 'keep'}});
  h.context.disconnectConfirmed();
  assert.deepEqual(h.users.data, {authEpoch: '1', unrelated: 'keep'});
  assert.equal(h.triggers.length, 0);
});

test('refresh can return a non-expiring OAuth token and clears old refresh metadata', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-a', tokenExpiresAt: String(Date.now() - 1000), refreshTokenExpiresAt: String(Date.now() + 10 * 86400000), authType: 'oauth'}, fetch: request => {
    if (request.url.endsWith('/login/oauth/access_token')) return {status: 200, body: {access_token: 'access-no-expiry'}};
    return {status: 200, body: {}};
  }});
  assert.equal(h.context.refreshAccessToken_(false), 'access-no-expiry');
  assert.equal(h.users.data.token, 'access-no-expiry');
  assert.equal(h.users.data.authType, 'oauth');
  assert.equal(h.users.data.refreshToken, undefined);
  assert.equal(h.users.data.tokenExpiresAt, undefined);
  assert.equal(h.users.data.refreshTokenExpiresAt, undefined);
});

test('an expired refresh-token lifetime requires reconnection without sending the token', () => {
  const h = harness({user: {token: 'access-a', refreshToken: 'refresh-secret', tokenExpiresAt: String(Date.now() - 1000), refreshTokenExpiresAt: String(Date.now() - 1000)}});
  assert.throws(() => h.context.refreshAccessToken_(false), /GitHub連携の期限が切れたため、公開が止まっています/);
  assert.equal(h.requests.length, 0);
  assert.equal(h.users.data.refreshToken, 'refresh-secret');
  assert.equal(h.users.data.reauthRequired, 'true');
  assert.equal(JSON.stringify(h.mails).includes('refresh-secret'), false);
});


test('overallStatus_ covers setup, reconnect, failure, stale, empty, pending, and healthy states', () => {
  const h = harness(); const c = h.context, p = h.shared;
  assert.equal(c.overallStatus_().level, 'none');
  p.setProperties({gistId:'g',worldUrl:'u',lastPublishOutcome:'reauth_required'});
  assert.deepEqual(plain(c.overallStatus_()), {level:'error',icon:'⚠',text:'GitHub連携の期限が切れたため、公開が止まっています',action:'管理者がメニュー「Utsushi → セットアップ・修復」を実行してください（URLは変わりません）'});
  p.setProperty('lastPublishOutcome','failure'); p.setProperty('lastPublishStatus','failure'); p.setProperty('lastError','2026-01-01T00:00:00.000Z 試験エラー');
  assert.match(c.overallStatus_().text,/試験エラー/);
  p.setProperty('lastPublishStatus','success'); p.setProperty('lastPublishOutcome','success'); p.setProperty('lastPublishMillis',String(Date.now()-91*60000)); p.setProperty('publishedNames','["board1"]');
  assert.equal(c.overallStatus_().level,'warn');
  p.deleteProperty('lastPublishMillis'); p.setProperty('publishedNames','[]');
  assert.match(c.overallStatus_().text,/範囲がありません/);
  p.setProperty('publishedNames','["board1"]'); p.setProperty('pending','queued');
  assert.equal(c.overallStatus_().icon,'⏳');
  p.deleteProperty('pending'); assert.equal(c.overallStatus_().level,'ok');
});

test('overallStatus_ keeps text and action distinct in every branch', () => {
  const branches = [
    p => {},
    p => p.setProperties({gistId:'g',worldUrl:'u',lastPublishOutcome:'reauth_required'}),
    p => p.setProperties({gistId:'g',worldUrl:'u',lastPublishStatus:'failure',lastError:'error'}),
    p => p.setProperties({gistId:'g',worldUrl:'u',lastPublishMillis:String(Date.now()-91*60000),publishedNames:'["board1"]'}),
    p => p.setProperties({gistId:'g',worldUrl:'u',publishedNames:'[]'}),
    p => p.setProperties({gistId:'g',worldUrl:'u',publishedNames:'["board1"]',pending:'queued'}),
    p => p.setProperties({gistId:'g',worldUrl:'u',publishedNames:'["board1"]'})
  ];
  for (const setup of branches) {
    const h = harness(), c = h.context;
    setup(h.shared);
    const status = c.overallStatus_();
    assert.notEqual(status.text, status.action);
  }
});
test('showStatus presents friendly state, a wired copy control, and localized error time without internal values', () => {
  const h = harness({doc:{gistId:'g',gistOwner:'admin',worldUrl:'https://example.test/world',lastPublishStatus:'failure',lastPublishOutcome:'failure',lastPublishMillis:String(Date.now()),lastPublishTime:'2026-01-02T03:04:00.000Z',updatedUnix:'1767323040',lastError:'2026-01-02T03:04:00.000Z 公開失敗',warnings:'[]',publishedNames:'["board1"]'}});
  h.context.showStatus(); const html=h.dialogs.at(-1)[1];
  assert.match(html,/id="copyworld" onclick="copyText\('worldUrl'\)"/); assert.match(html,/class="status/);
  for(const secret of ['success','failure','状態未記録','2026-01-02T']) assert.equal(html.includes(secret),false);
  assert.match(html,/2026\/01\/02 12:04/);
  assert.match(html,/掲示板URL（ワールド作者に渡します）/);
  assert.equal(html.includes('直近のエラー'),false);
  assert.equal((html.match(/公開失敗/g)||[]).length,1);
});

test('showStatus lists an error notice when the state block does not contain it', () => {
  const h=harness({doc:{gistId:'g',worldUrl:'u',lastPublishStatus:'success',lastPublishOutcome:'success',lastError:'2026-01-02T03:04:00.000Z URL変更のお知らせメールを送れませんでした。',warnings:'[]',publishedNames:'["board1"]'}});
  h.context.showStatus();
  const html=h.dialogs.at(-1)[1];
  assert.equal(html.includes('直近のエラー'),false);
  assert.match(html,/<h3>お知らせ<\/h3><ul><li>2026\/01\/02 12:04 URL変更のお知らせメールを送れませんでした。<\/li>/);
});

test('finishSetup returns a zero range count and finish targets exist in every setup screen', () => {
  const h=harness(); h.context.readRanges_=()=>({ranges:{},warnings:[],rowStatus:[]}); h.shared.setProperties({gistId:'g',gistOwner:'admin',worldUrl:'https://example.test/world'});
  h.context.checkGistOwner_=()=>{}; h.context.publish_=()=>{h.shared.setProperty('publishedNames','[]');return true;};
  const result=h.context.finishSetup(); assert.equal(result.rangeCount,0); assert.equal(result.url,'https://example.test/world');
  h.context.showFinishDialog_();
  const finishHtml=h.dialogs.at(-1)[1];
  assert.match(finishHtml,/<div id="result" style="display:none"><\/div>/);
  assert.match(finishHtml,/function finish\(confirmed\)/);
  assert.match(finishHtml,/掲示板に出るのは、設定タブに書いた範囲だけです。/);
  assert.match(finishHtml,/次にやること/);
  assert.match(finishHtml,/document\.getElementById\("copyurl"\)\.onclick=function\(\)\{copyText\("url"\)\}/);

  const tokenless=harness({clientId:''});
  tokenless.context.beginAuthorization_(true);
  const tokenHtml=tokenless.dialogs.at(-1)[1];
  assert.match(tokenHtml,/<div id="result" style="display:none"><\/div>/);
  assert.match(tokenHtml,/saveToken/);

  const device=harness({fetch:request=>request.url.endsWith('/device/code')
    ? {status:200,body:{device_code:'device',user_code:'ABCD',verification_uri:'https://github.com/login/device',interval:5}}
    : {status:200,body:{}}});
  device.context.beginAuthorization_(true);
  const deviceHtml=device.dialogs.at(-1)[1];
  assert.match(deviceHtml,/<div id="result" style="display:none"><\/div>/);
  assert.match(deviceHtml,/if\(r.status==="success"\)\{\(r\.handoff\?/);
  assert.match(deviceHtml,/onclick="copyCode\(\)"/);
  for (const html of [finishHtml,tokenHtml,deviceHtml]) {
    assert.equal((html.match(/function fail\(/g)||[]).length,1);
    for (const match of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
  }

  const elements={
    setupFlow:{style:{}},
    result:{style:{},innerHTML:''},
    status:{textContent:''},
    url:{value:''}
  };
  device.context.document={getElementById:id=>elements[id] || (elements[id]={style:{},value:'',innerHTML:''}),querySelector:()=>null};
  device.context.event={currentTarget:{disabled:false}};
  device.context.google={script:{run:{
    withSuccessHandler:callback=>({withFailureHandler:()=>({finishSetup:()=>callback({url:'https://example.test/world',rangeCount:0})})})
  }}};
  vm.runInContext(device.context.clientHelpers_(),device.context);
  device.context.finish();
  assert.equal(elements.setupFlow.style.display,'none');
  assert.equal(elements.result.style.display,'block');
  assert.equal(elements.url.value,'https://example.test/world');
  assert.match(elements.result.innerHTML,/次にやること/);
});

test('user-facing wording avoids the public URL warning and internal error terms', () => {
  assert.equal(script.includes('公開内容はURLを知っている人なら誰でも読めます。'), false);
  const tokenless = harness({clientId: ''});
  tokenless.context.beginAuthorization_(false);
  const device = harness({fetch: request => request.url.endsWith('/device/code')
    ? {status: 200, body: {device_code: 'device', user_code: 'ABCD', verification_uri: 'https://github.com/login/device', interval: 5}}
    : {status: 200, body: {}}});
  device.context.beginAuthorization_(true);
  const finish = harness(); finish.context.showFinishDialog_();
  for (const h of [tokenless, device, finish]) {
    assert.equal(h.dialogs.at(-1)[1].includes('公開内容はURLを知っている人なら誰でも読めます。'), false);
  }
  assert.equal(script.includes('再試行してください'), false);
  const userFacingSource = script
    .replace(/\/\/[^\r\n]*/g, '')
    .replace('GitHubで作成した、gist権限のあるPersonal access token（classic）を貼り付けてください。', '');
  for (const term of ['OAuth', 'HTTP', '500 KB']) assert.equal(userFacingSource.includes(term), false, term);
});

test('setup by a tokenless staff member only shows the handoff explanation', () => {
  const h=harness({user:{},doc:{gistId:'existing',gistOwner:'admin',worldUrl:'old'}}); h.context.startSetup();
  assert.equal(h.requests.length,0); assert.match(h.dialogs.at(-1)[1],/スタッフの方は操作不要です/); assert.match(h.dialogs.at(-1)[1],/startSetupConfirmed/);
  assert.deepEqual(plain(h.dialogs.at(-1)[2]),{close:false});
});

test('destination recreation only displays confirmation before any GitHub request and hides the new URL until success', () => {
  const h=harness({user:{},doc:{gistId:'existing',gistOwner:'admin',worldUrl:'old'}}); h.context.recreateDestination();
  assert.equal(h.requests.length,0); const html=h.dialogs.at(-1)[1];
  assert.match(html,/admin/); assert.match(html,/更新されなくなります/); assert.match(html,/id="run" disabled/);
  assert.match(html,/id="newurlresult" style="display:none"/);
  assert.ok(html.indexOf('x.style.display="block"') < html.indexOf('x.innerHTML="<input id=newurl'));
  assert.match(html,/id=copynew/); assert.match(html,/copyText\("newurl"\)/);
  for (const match of html.matchAll(/<script>([\s\S]*?)<\/script>/g)) new vm.Script(match[1]);
});

test('errorText_ preserves source wording and only treats GitHub 5xx errors as temporary', () => {
  const {context:c}=harness();
  assert.match(c.errorText_(Object.assign(new Error('bad'),{status:403})),/エラー番号 403/);
  assert.equal(c.errorText_(Object.assign(new Error('更新が混み合っています。'),{status:503})),'GitHubに公開できませんでした（エラー番号 503）。メニュー「Utsushi → 状態とURLを表示」の内容を配布者に伝えてください。');
  assert.match(c.errorText_(Object.assign(new Error('GitHubへ接続できません'),{status:503,githubOrigin:true})),/GitHubが一時的に応答していません/);
  assert.equal(c.errorText_(new Error('OAuth token scope')),'処理できませんでした。しばらくしても直らない場合は、メニュー「Utsushi → 状態とURLを表示」の内容を配布者に伝えてください。');
  assert.match(c.errorText_(Object.assign(new Error('bad'),{status:422})),/エラー番号 422/);
  assert.match(c.errorText_(Object.assign(new Error('unauthorized_client'),{status:403})),/エラー番号 403/);
});

test('failure email gives a clear subject and numbered next steps', () => {
  const h=harness({doc:{gistId:'g',gistOwner:'admin',worldUrl:'u',lastPublishStatus:'failure',lastPublishOutcome:'failure',lastError:'2026-01-01T00:00:00.000Z test'}});
  h.context.notifyError_(Object.assign(new Error('x'),{status:403}),'friendly',h.shared);
  assert.equal(h.mails[0][1],'Utsushi: 掲示板の公開が止まっています'); assert.match(h.mails[0][2],/1\./); assert.match(h.mails[0][2],/https:\/\/example.test\/sheet/);
});


test('readRanges_ preserves tab names and returns row statuses for invalid and valid rows', () => {
  const h=harness(), c=h.context;
  const config=[['#sample','Source!A1'],['ok','Source!A1','',false,false,'',''],['ok','Source!A1','',false,false,'',''],['','Source!A1'],['missing','Gist!A1'],['badopts','Source!A1','',false,false,'oops','wrong'],['valid','Source!A1','',false,false,'','']];
  const settings={getLastRow:()=>8,getRange:(r,col,n)=>({getValues:()=>config})};
  const cell={getRow:()=>1,getColumn:()=>1,getNumRows:()=>1,getNumColumns:()=>1,getDisplayValues:()=>[['x']],getDataValidations:()=>[[null]]};
  const source={getName:()=> 'Source',getLastRow:()=>2,getLastColumn:()=>2,getRange:()=>cell,isRowHiddenByUser:()=>false,isRowHiddenByFilter:()=>false,isColumnHiddenByUser:()=>false};
  const ss={getSheetByName:n=>n==='Utsushi設定'?settings:n==='Source'?source:null,getSheets:()=>[source]};
  c.SpreadsheetApp.getActiveSpreadsheet=()=>ss; c.readRanges_=c.__readRanges_;
  const result=c.readRanges_();
  assert.deepEqual(Object.keys(plain(result.ranges)), ['ok', 'badopts', 'valid']);
  assert.equal(result.rowStatus[0],'非公開（名前の先頭が # のため）');
  assert.match(result.rowStatus[2],/同じ名前が上の行/); assert.match(result.rowStatus[3],/名前（A列）が空/);
  assert.match(result.rowStatus[4],/シート「Gist」が見つかりません/);
  assert.match(result.rowStatus[5],/列幅の比の書き方が違うため無視しました/); assert.match(result.rowStatus[5],/配置の書き方が違うため無視しました/);
  assert.match(result.rowStatus[6],/✔ 掲示板に表示されます/);
});

test('readRanges_ omits row-height and column-width reads when formatting is disabled', () => {
  const h = harness(), c = h.context;
  let rowHeightCalls = 0, columnWidthCalls = 0;
  const config = [['range', 'Source!A1', '', false, false, '', '']];
  const settings = {getLastRow: () => 2, getRange: () => ({getValues: () => config})};
  const cell = {getRow: () => 1, getColumn: () => 1, getNumRows: () => 1, getNumColumns: () => 1,
    getDisplayValues: () => [['x']], getDataValidations: () => [[null]]};
  const source = {getName: () => 'Source', getLastRow: () => 1, getLastColumn: () => 1, getRange: () => cell,
    isRowHiddenByUser: () => false, isRowHiddenByFilter: () => false, isColumnHiddenByUser: () => false,
    getRowHeight: () => { rowHeightCalls++; return 21; }, getColumnWidth: () => { columnWidthCalls++; return 80; }};
  c.SpreadsheetApp.getActiveSpreadsheet = () => ({getSheetByName: n => n === 'Utsushi設定' ? settings : source, getSheets: () => [source]});
  c.readRanges_();
  assert.equal(rowHeightCalls, 0);
  assert.equal(columnWidthCalls, 0);
});

test('writeSheetStatus_ never rereads ranges and batches changed H and J values', () => {
  const h=harness({doc:{rowStatus:'["✔ ok"]'}});
  const values=new Map([['1,8','状態'],['2,8','old'],['1,10','Utsushi の状態'],['2,10','old']]);
  const writes=[];
  const sheet={getLastRow:()=>2,getRange:(r,c,n)=>({
    getValues:()=>Array.from({length:n||1},(_,i)=>[values.get((r+i)+','+c)||'']),
    setValues:rows=>{writes.push([r,c,rows]);rows.forEach((row,i)=>values.set((r+i)+','+c,row[0]));}
  })};
  h.context.SpreadsheetApp.getActiveSpreadsheet=()=>({getSheetByName:()=>sheet});
  h.context.readRanges_=()=>{throw new Error('writeSheetStatus_ must use cached statuses');};
  h.context.writeSheetStatus_();
  assert.deepEqual(writes.map(([,column])=>column),[8,10]);
  assert.equal(writes[0][2].length,2);
  assert.equal(writes[1][2].length,4);
  const changed=writes.length;
  h.context.writeSheetStatus_();
  assert.equal(writes.length,changed);
});

test('writeSheetStatus_ clears stale H values below cached configuration rows in one batch', () => {
  const h=harness({doc:{rowStatus:'["✔ ok"]'}});
  const values=new Map([['1,8','状態'],['2,8','✔ ok'],['3,8','stale'],['4,8','stale']]);
  const writes=[];
  const sheet={getLastRow:()=>4,getRange:(r,c,n)=>({
    getValues:()=>Array.from({length:n||1},(_,i)=>[values.get((r+i)+','+c)||'']),
    setValues:rows=>{writes.push([r,c,rows]);rows.forEach((row,i)=>values.set((r+i)+','+c,row[0]));}
  })};
  h.context.SpreadsheetApp.getActiveSpreadsheet=()=>({getSheetByName:()=>sheet});
  h.context.writeSheetStatus_();
  const rowsWrite=writes.find(([,column])=>column===8);
  assert.deepEqual(plain(rowsWrite[2]),[['状態'],['✔ ok'],[''],['']]);
});

test('setPending_ writes status from properties without calling readRanges_', () => {
  const h=harness({doc:{rowStatus:'["waiting"]'}});
  const writes=[];
  const sheet={getLastRow:()=>2,getRange:(r,c,n)=>({
    getValues:()=>Array.from({length:n||1},()=>['']),
    setValues:rows=>writes.push([r,c,rows])
  })};
  h.context.SpreadsheetApp.getActiveSpreadsheet=()=>({getSheetByName:()=>sheet});
  h.context.readRanges_=()=>{throw new Error('setPending_ must not reread ranges');};
  h.context.setPending_();
  assert.equal(h.shared.data.pending,'pending-1');
  assert.deepEqual(plain(writes.find(([,column])=>column===8)[2]),[['状態'],['waiting']]);
});

test('tokenless addSelectionToBoard marks its new row as waiting for the administrator', () => {
  const h=harness({user:{},doc:{rowStatus:'[]'}});
  const source={getName:()=> 'Source'};
  const selected={getNumRows:()=>2,getNumColumns:()=>2,getA1Notation:()=> 'A1:B2',getSheet:()=>source};
  const rows=[['#sample','Source!A1:F20','',false,true]];
  const values=new Map();
  const settings={
    getLastRow:()=>2,
    getRange:(r,c,n)=>({
      getValues:()=>c===1?rows:Array.from({length:n||1},(_,i)=>[values.get((r+i)+','+c)||'']),
      setValues:next=>{
        if(c===1) rows[r-2]=next[0];
        else next.forEach((row,i)=>values.set((r+i)+','+c,row[0]));
      }
    })
  };
  const ss={getActiveRange:()=>selected,getActiveSheet:()=>source,getSheetByName:n=>n==='Utsushi設定'?settings:null,
    getSheets:()=>[source],setActiveSheet:()=>{},getSpreadsheetTimeZone:()=> 'Asia/Tokyo'};
  h.context.SpreadsheetApp.getActiveSpreadsheet=()=>ss;
  h.context.ensureSettingsSheet_=()=>{};
  h.context.readRanges_=()=>{throw new Error('tokenless add must not read ranges');};
  h.context.addSelectionToBoard();
  assert.equal(values.get('2,8'),'⏳ 管理者の自動公開を待っています');
});

test('onEditInstalled ignores status-column edits on the settings tab', () => {
  const h=harness(); const e={range:{getSheet:()=>({getName:()=> 'Utsushi設定'}),getColumn:()=>8}}; h.context.onEditInstalled(e);
  assert.equal(h.requests.length,0); assert.equal(h.lockWaits.length,0);
});

test('status sheet write failures never turn a successful publication into failure', () => {
  const h=harness(); h.context.SpreadsheetApp.getActiveSpreadsheet=()=>({getName:()=> 'テストシート',getUrl:()=> 'https://example.test/sheet',getSpreadsheetTimeZone:()=> 'Asia/Tokyo',getSheetByName:()=>({getLastRow:()=>1,getRange:(r,c,n)=>({getValues:()=>Array.from({length:n||1},()=>['']),setValues:()=>{throw Error('protected')}})})});
  assert.equal(h.context.publish_(true),true);
});


test('addSelectionToBoard replaces the sample then adds board2, with staff guidance for invalid selections and protected settings', () => {
  const h=harness(); const c=h.context; let rows=[['#sample','Source!A1:F20','',false,true]]; const written=[]; let protectedWrite=false;
  const source={getName:()=> 'Source'}; let active=source; let selected={getNumRows:()=>2,getNumColumns:()=>3,getA1Notation:()=> 'B2:D3',getSheet:()=>source};
  const settings={getLastRow:()=>rows.length+1,getRange:(r,col,n)=>({getValues:()=>rows,setValues:v=>{if(protectedWrite)throw Error('protected');written.push([r,v]);rows[r-2]=v[0]}})};
  const ss={getActiveRange:()=>selected,getActiveSheet:()=>active,getSheetByName:n=>n==='Utsushi設定'?settings:null,getSheets:()=>[source],setActiveSheet:()=>{}};
  c.SpreadsheetApp.getActiveSpreadsheet=()=>ss;c.ensureSettingsSheet_=()=>{};c.writeSheetStatus_=()=>{};c.publish_=()=>true;
  c.addSelectionToBoard();assert.equal(written[0][1][0][0],'board1');assert.equal(written[0][1][0][1],'Source!B2:D3');
  c.addSelectionToBoard();assert.equal(written[1][1][0][0],'board2');
  active={getName:()=> 'Utsushi設定'};c.addSelectionToBoard();assert.match(h.dialogs.at(-1)[1],/表全体をドラッグで選択/);
  active=source;selected={getNumRows:()=>1,getNumColumns:()=>1,getA1Notation:()=> 'A1',getSheet:()=>source};c.addSelectionToBoard();assert.match(h.dialogs.at(-1)[1],/メニュー「Utsushi → 選択範囲を掲示板に追加」をもう一度実行/);
  selected={getNumRows:()=>2,getNumColumns:()=>2,getA1Notation:()=> 'A1:B2',getSheet:()=>source};protectedWrite=true;c.addSelectionToBoard();assert.match(h.dialogs.at(-1)[1],/設定タブは管理者だけが編集できます/);
});

test('setup menu uses the unified label and still calls startSetup', () => {
  const h = harness(), items = [], adminItems = [];
  const admin = {addItem: (label, handler) => {adminItems.push([label, handler]); return admin;}};
  const menu = {addItem: (label, handler) => {items.push([label, handler]); return menu;}, addSubMenu: () => menu, addToUi: () => {}};
  h.context.SpreadsheetApp.getUi = () => ({createMenu: name => name === 'Utsushi' ? menu : admin});
  h.context.onOpen();
  assert.deepEqual(items[0], ['セットアップ・修復', 'startSetup']);
  assert.deepEqual(adminItems.map(item => item[0]), ['GitHub連携をやり直す', '公開先を作り直す', '連携を解除']);
});

test('startSetup routes healthy tokens to finish and authorization problems to GitHub', () => {
  const cases = [
    {user: {token: 't'}, doc: {}, expected: 'セットアップ'},
    {user: {token: 't', reauthRequired: 'true'}, doc: {}, expected: 'GitHub連携'},
    {user: {token: 't'}, doc: {lastPublishOutcome: 'reauth_required'}, expected: 'GitHub連携'},
    {user: {token: 't'}, doc: {lastPublishStatus: 'failure', lastError: '失敗（エラー番号 403）'}, expected: 'GitHub連携'},
    {user: {token: 't'}, doc: {lastPublishStatus: 'failure', lastError: '許可がありません'}, expected: 'GitHub連携'}
  ];
  for (const item of cases) {
    const h = harness({user: item.user, doc: item.doc, fetch: request => request.url.endsWith('/device/code')
      ? {status: 200, body: {device_code: 'device', user_code: 'ABCD', verification_uri: 'https://github.com/login/device', interval: 5}}
      : {status: 200, body: {}}});
    h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({getActiveSheet: () => ({getName: () => 'Source'}), getActiveRange: () => null});
    h.context.startSetup();
    assert.equal(h.dialogs.at(-1)[0], item.expected);
  }
});

test('startSetup records only multi-cell selections outside the settings tab', () => {
  for (const selected of [
    {name: 'Source', rows: 1, cols: 1, expected: null},
    {name: 'Utsushi設定', rows: 2, cols: 2, expected: null},
    {name: 'Source', rows: 2, cols: 1, expected: 'Source!A1:A2'}
  ]) {
    const h = harness({user: {token: 't'}});
    const sheet = {getName: () => selected.name};
    h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({getActiveSheet: () => sheet, getActiveRange: () => ({
      getNumRows: () => selected.rows, getNumColumns: () => selected.cols, getA1Notation: () => 'A1' + (selected.rows > 1 ? ':A2' : '')
    })});
    h.context.recordSetupSelection_();
    assert.equal(h.users.getProperty('setupSelection'), selected.expected);
  }
});

test('finishSetup replaces the sample with the selected range, publishes it, and reports the added name', () => {
  const h = harness({user: {token: 't', setupSelection: 'Source!B2:D3'}, doc: {gistId: 'g', worldUrl: 'https://example.test/world'}});
  const rows = [['#sample', 'Source!A1:F20', '', false, true, '', '']];
  const settings = {getLastRow: () => rows.length + 1, getRange: (r, c, n, width) => ({
    getValues: () => c === 1 && width === 1 ? rows.map(row => [row[0]]) : rows,
    setValues: values => {rows[r - 2] = [...values[0], '', ''];}
  })};
  const source = {getName: () => 'Source'};
  const ss = {getSheetByName: name => name === 'Utsushi設定' ? settings : null, getSheets: () => [source], setActiveSheet: () => {}};
  h.context.SpreadsheetApp.getActiveSpreadsheet = () => ss;
  h.context.checkGistOwner_ = () => {};
  h.context.installTriggers_ = () => {};
  let published = false;
  h.context.publish_ = () => {published = true; h.shared.setProperties({publishedNames: '["board1"]', publishedNameCount: '1'}); return true;};
  h.context.worldUrl_ = () => 'https://example.test/world';
  h.context.writeSheetStatus_ = () => {};
  const result = plain(h.context.finishSetup());
  assert.equal(rows[0][0], 'board1');
  assert.equal(rows[0][1], 'Source!B2:D3');
  assert.equal(published, true);
  assert.equal(result.addedName, 'board1');
  assert.equal(result.addedRange, 'Source!B2:D3');
  assert.equal(result.repaired, true);
  assert.equal(h.users.getProperty('setupSelection'), null);
});

test('finishSetup does not add a row for existing ranges, empty selections, or settings-tab selections', () => {
  for (const scenario of [
    {selection: 'Source!A1:B2', rows: [['board1', 'Source!A1']]},
    {selection: '', rows: [['#sample', 'Source!A1:F20']]},
    {selection: 'Utsushi設定!A1:B2', rows: [['#sample', 'Source!A1:F20']]}
  ]) {
    const h = harness({user: {token: 't', setupSelection: scenario.selection}, doc: {worldUrl: 'url'}});
    const rows = scenario.rows.map(row => [...row, '', false, true, '', '']);
    const settings = {getLastRow: () => rows.length + 1, getRange: (r, c, n, width) => ({
      getValues: () => c === 1 && width === 1 ? rows.map(row => [row[0]]) : rows,
      setValues: values => {rows[r - 2] = values[0];}
    })};
    h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({getSheetByName: () => settings, getSheets: () => [{getName: () => 'Source'}],
      getActiveSheet: () => ({getName: () => scenario.selection.startsWith('Utsushi設定!') ? 'Utsushi設定' : 'Source'}),
      getActiveRange: () => ({getNumRows: () => 2, getNumColumns: () => 2, getA1Notation: () => 'A1:B2'}), setActiveSheet: () => {}});
    if (scenario.selection.startsWith('Utsushi設定!')) h.context.recordSetupSelection_();
    h.context.checkGistOwner_ = () => {}; h.context.installTriggers_ = () => {};
    h.context.publish_ = () => {h.shared.setProperties({publishedNames: '["board1"]', publishedNameCount: '1'}); return true;};
    h.context.worldUrl_ = () => 'url'; h.context.writeSheetStatus_ = () => {};
    const result = plain(h.context.finishSetup());
    assert.equal(result.addedName, '');
    assert.equal(result.addedRange, '');
    assert.equal(result.repaired, false);
    assert.equal(rows.length, scenario.rows.length);
  }
});

test('new settings tabs group and collapse columns F and G', () => {
  const h = harness(); let grouped = false, collapsed = false, existing = false, groups = 0;
  const sheet = {
    getMaxRows: () => 100, getRange: () => ({setNumberFormat: () => {}, insertCheckboxes: () => {}, setValues: () => {}, setNote: () => {}, shiftColumnGroupDepth: depth => {groups++; grouped = depth === 1;}}),
    setFrozenRows: () => {}, setColumnWidth: () => {}, getColumnGroup: (column, depth) => ({collapse: () => {groups++; collapsed = column === 6 && depth === 1;}})
  };
  const source = {getName: () => 'Source'};
  h.context.SpreadsheetApp.getActiveSpreadsheet = () => ({getSheetByName: () => existing ? sheet : null, getSheets: () => [source], insertSheet: () => {existing = true; return sheet;}});
  h.context.__ensureSettingsSheet_();
  assert.equal(grouped, true);
  assert.equal(collapsed, true);
  h.context.__ensureSettingsSheet_();
  assert.equal(groups, 2);
});
