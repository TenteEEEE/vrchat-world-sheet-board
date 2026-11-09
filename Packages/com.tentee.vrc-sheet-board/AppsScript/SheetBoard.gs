/**
 * SheetBoard: シートの表をワールドに公開するためのスクリプトです。
 * シートを開き直し、メニューの「はじめにセットアップ」から始めてください。
 */
const GITHUB_CLIENT_ID = 'Ov23liVU3bFAK2lWtKPk'; // Sheet Board OAuth App（Device Flow）の公開クライアントIDです。秘密情報ではありません。フォークでは独自のIDに置き換えられます。空にするとトークン貼り付け方式になります。
const GIST_FILENAME = 'sheetboard.json';
const SETTINGS_SHEET = 'SheetBoard設定';
const HEARTBEAT_MINUTES = 60;
const TOKEN_REFRESH_MARGIN_MS = 5 * 60 * 1000;
const MAX_STATUS_ITEM_CHARS = 120;
const PROPERTY_VALUE_MAX_BYTES = 8000;
const EXECUTION_BUDGET_MS = 270000;
const GIST_RAW_MAX_BYTES = 10 * 1024 * 1024;
const IOS_TRUNCATION_WARN_BYTES = 800 * 1024;
const LARGE_RANGE_CELLS = 5000;

function nowMillis_() { return Date.now(); }

function utf8ByteLength_(value) {
  return Utilities.newBlob(String(value)).getBytes().length;
}

function setBoundedProperty_(props, key, value) {
  const text = String(value);
  if (utf8ByteLength_(text) > PROPERTY_VALUE_MAX_BYTES) return false;
  try {
    props.setProperty(key, text);
    return true;
  } catch (e) {
    return false;
  }
}

function setPublishedNames_(props, names) {
  const saved = [];
  for (const name of names || []) {
    const candidate = saved.concat([name]);
    if (utf8ByteLength_(JSON.stringify(candidate)) > PROPERTY_VALUE_MAX_BYTES) break;
    saved.push(name);
  }
  setBoundedProperty_(props, 'publishedNames', JSON.stringify(saved));
  setBoundedProperty_(props, 'publishedNameCount', String((names || []).length));
  setBoundedProperty_(props, 'publishedNamesIncomplete', String(saved.length < (names || []).length));
}

function publishedNameCount_(props) {
  return Number(props.getProperty('publishedNameCount') || parseJsonArray_(props.getProperty('publishedNames')).length);
}

function checkExecutionBudget_(startedAt) {
  if (startedAt !== null && startedAt !== undefined && nowMillis_() - startedAt > EXECUTION_BUDGET_MS) {
    const error = new Error('表が大きすぎて、時間内に読み切れませんでした。範囲を分けるか、使わない列・行を範囲から外してください。（エラー番号 408）');
    error.status = 408;
    error.executionBudgetExceeded = true;
    throw error;
  }
}
function trimEmpty(values, layers) {
  layers = layers || {};
  values = values || [];
  let rows = values.length;
  let cols = 0;
  function occupied(r, c) {
    const value = (values[r] || [])[c];
    const bg = layers.bg && layers.bg[r] && layers.bg[r][c];
    return (value !== '' && value !== null && value !== undefined) || !!bg;
  }
  for (let r = 0; r < rows; r++) {
    const width = Math.max((values[r] || []).length, (layers.bg && layers.bg[r] || []).length);
    for (let c = 0; c < width; c++) {
      if (occupied(r, c)) cols = Math.max(cols, c + 1);
    }
  }
  while (rows > 0 && !Array.from({length: cols}, (_, c) => occupied(rows - 1, c)).some(Boolean)) rows--;
  function crop(matrix, fallback) {
    return Array.from({length: rows}, (_, r) => Array.from({length: cols}, (_, c) =>
      matrix && matrix[r] && matrix[r][c] !== undefined ? matrix[r][c] : fallback));
  }
  const result = {};
  Object.keys(layers).forEach(key => { result[key] = crop(layers[key], ''); });
  return {values: crop(values, ''), layers: result};
}

function parseJsonArray_(text) {
  try {
    const value = JSON.parse(text || '[]');
    return Array.isArray(value) ? value : [];
  } catch (e) {
    return [];
  }
}

function boundedStatusArray_(values) {
  const result = (values || []).map(value => String(value || '').slice(0, MAX_STATUS_ITEM_CHARS));
  for (let i = result.length - 1; i >= 0 && utf8ByteLength_(JSON.stringify(result)) > PROPERTY_VALUE_MAX_BYTES; i--) result[i] = '';
  return result;
}

function generalAlign(displayValue) {
  const value = String(displayValue === null || displayValue === undefined ? '' : displayValue)
    .replace(/[\s,]/g, '').replace(/^[¥$€£]/, '').replace(/%$/, '').replace(/^[+-]/, '');
  return value && Number.isFinite(Number(value)) ? 'r' : 'l';
}

function mapAlign(alignment, displayValue) {
  const value = String(alignment || '').toLowerCase();
  if (value === 'left' || value === 'general-left') return 'l';
  if (value === 'center' || value === 'general-center') return 'c';
  if (value === 'right' || value === 'general-right') return 'r';
  return generalAlign(displayValue);
}

function styleBits(weight, fontStyle, fontLine) {
  const line = String(fontLine || '').toLowerCase();
  return (weight === 'bold' ? 1 : 0) | (fontStyle === 'italic' ? 2 : 0) |
    (line.indexOf('underline') >= 0 ? 4 : 0) | (line.indexOf('line-through') >= 0 ? 8 : 0);
}

function hiddenFilter(matrix, keepRows, keepCols) {
  return (matrix || []).filter((_, r) => keepRows[r]).map(row => (row || []).filter((_, c) => keepCols[c]));
}

function remapMerges(merges, keepRows, keepCols) {
  const rows = keepRows.map((keep, i) => keep ? keepRows.slice(0, i).filter(Boolean).length : -1);
  const cols = keepCols.map((keep, i) => keep ? keepCols.slice(0, i).filter(Boolean).length : -1);
  const result = [];
  (merges || []).forEach(merge => {
    const rs = rows.slice(Math.max(0, merge[0]), Math.max(0, merge[0] + merge[2])).filter(i => i >= 0);
    const cs = cols.slice(Math.max(0, merge[1]), Math.max(0, merge[1] + merge[3])).filter(i => i >= 0);
    if (rs.length && cs.length && (rs.length > 1 || cs.length > 1)) result.push([rs[0], cs[0], rs.length, cs.length]);
  });
  return result;
}

function parseRatioList(text) {
  if (text === null || text === undefined || String(text).trim() === '') return [];
  const parts = String(text).split(',');
  const numbers = parts.map(s => Number(s.trim()));
  return parts.some(s => !s.trim()) || numbers.some(n => !Number.isFinite(n) || n <= 0) ? [] : numbers;
}

function parseAlignList(text) {
  if (text === null || text === undefined || String(text).trim() === '') return [];
  const parts = String(text).split(',').map(s => s.trim().toLowerCase());
  return parts.some(s => !['l', 'c', 'r'].includes(s)) ? [] : parts;
}

function parseRangeRef(text) {
  const value = String(text === null || text === undefined ? '' : text).trim();
  if (!value) return null;
  const separator = value.lastIndexOf('!');
  if (separator < 0) return {sheet: '', a1: value};
  let sheet = value.slice(0, separator).trim();
  if (sheet[0] === "'") sheet = sheet.slice(1);
  if (sheet.endsWith("'")) sheet = sheet.slice(0, -1);
  return {sheet: sheet.replace(/''/g, "'"), a1: value.slice(separator + 1).trim()};
}

function normalizeColor_(hex, excluded) {
  let value = String(hex || '').trim();
  if (/^#[0-9a-f]{3}$/i.test(value)) value = '#' + value.slice(1).split('').map(c => c + c).join('');
  if (!/^#[0-9a-f]{6}$/i.test(value)) return '';
  value = value.toUpperCase();
  return value === excluded ? '' : value;
}

function normalizeColor(hex) { return normalizeColor_(hex, '#000000'); }
function normalizeBackground_(hex) { return normalizeColor_(hex, '#FFFFFF'); }

function chipSwatch(displayValues, backgrounds, fontColors, value) {
  const target = String(value).trim();
  for (let r = 0; r < (displayValues || []).length; r++) {
    const displayRow = displayValues[r] || [];
    for (let c = 0; c < displayRow.length; c++) {
      if (String(displayRow[c]).trim() !== target) continue;
      const bg = backgrounds && backgrounds[r] ? backgrounds[r][c] : '';
      const fg = fontColors && fontColors[r] ? fontColors[r][c] : '';
      return {bg: normalizeBackground_(bg), fg: normalizeColor(fg)};
    }
  }
  return null;
}

function chipCode(value, showDropdown, swatch) {
  if (showDropdown === false) return '';
  if (value === '' || value === null || value === undefined) return 'a';
  if (!swatch || (!swatch.bg && !swatch.fg)) return 'c';
  return 'c|' + (swatch.bg || '') + '|' + (swatch.fg || '');
}

function overallStatus_() {
  const props = PropertiesService.getDocumentProperties();
  const outcome = props.getProperty('lastPublishOutcome');
  const status = props.getProperty('lastPublishStatus');
  let warnings = [];
  try {
    warnings = JSON.parse(props.getProperty('warnings') || '[]');
  } catch (e) {}
  const names = (() => {
    try {
      return JSON.parse(props.getProperty('publishedNames') || '[]');
    } catch (e) {
      return [];
    }
  })();
  let result;
  if (!props.getProperty('gistId') && !props.getProperty('worldUrl')) {
    result = {level: 'none', icon: '○', text: 'まだセットアップされていません', action: '管理者がメニュー「SheetBoard → はじめにセットアップ」を実行してください'};
  } else if (outcome === 'reauth_required') {
    result = {level: 'error', icon: '⚠', text: 'GitHub連携の期限が切れたため、公開が止まっています', action: '管理者がメニュー「SheetBoard → 管理者用 → GitHub連携をやり直す」を実行してください（URLは変わりません）'};
  } else if (status === 'failure') {
    const error = String(props.getProperty('lastError') || '').replace(/^\d{4}-\d\d-\d\dT[^ ]+\s*/, '');
    result = {level: 'error', icon: '⚠', text: '公開できませんでした: ' + error, action: errorAction_(error)};
  } else if (Number(props.getProperty('lastPublishMillis') || 0) && nowMillis_() - Number(props.getProperty('lastPublishMillis')) > 90 * 60000) {
    result = {level: 'warn', icon: '⚠', text: '自動公開が止まっているようです', action: '管理者がメニュー「SheetBoard → はじめにセットアップ」をもう一度実行してください'};
  } else if (!publishedNameCount_(props)) {
    result = {level: 'warn', icon: '⚠', text: '掲示板に出す範囲がありません', action: '設定タブのA列（名前）の先頭の # を消してください。範囲を追加するには、表を選択してメニュー「SheetBoard → 選択範囲を掲示板に追加」'};
  } else if (props.getProperty('pending')) {
    result = {level: 'warn', icon: '⏳', text: '編集の反映を待っています（数分以内に自動で公開されます）', action: ''};
  } else {
    result = {level: 'ok', icon: '✔', text: '正常に公開されています', action: ''};
  }
  if ((result.level === 'ok' || result.level === 'warn') && warnings.length) result.text += '（お知らせがあります）';
  return result;
}
function errorAction_(message) {
  if (/403|許可がありません|権限/.test(message)) return permissionMessage_();
  if (/429|5\d\d|一時的/.test(message)) return '自動で再試行します。操作は不要です。しばらくしても直らない場合は、メニュー「SheetBoard → 状態とURLを表示」の内容を配布者に伝えてください';
  return 'しばらくしても直らない場合は、メニュー「SheetBoard → 状態とURLを表示」の内容を配布者に伝えてください';
}
function reconnectMessage_(staff) {
  return 'GitHub連携の期限が切れたため、公開が止まっています。' + (staff ? '管理者が' : '') + 'メニュー「SheetBoard → 管理者用 → GitHub連携をやり直す」を実行してください（URLは変わりません）。';
}
function permissionMessage_() {
  return 'GitHubで公開する許可がありません。管理者がメニュー「SheetBoard → 管理者用 → GitHub連携をやり直す」で、もう一度承認してください';
}
function writeSheetStatus_() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const sheet = ss.getSheetByName(SETTINGS_SHEET);
  if (!sheet) return;
  const props = PropertiesService.getDocumentProperties();
  const rowStatus = parseJsonArray_(props.getProperty('rowStatus'));
  const statusRows = Math.max(0, sheet.getLastRow() - 1);
  const header = sheet.getRange(1, 8).getValues();
  const currentRows = statusRows ? sheet.getRange(2, 8, statusRows, 1).getValues() : [];
  const nextRows = Array.from({length: statusRows}, (_, index) => [rowStatus[index] || '']);
  if (header[0][0] !== '状態' || JSON.stringify(currentRows) !== JSON.stringify(nextRows)) {
    try { sheet.getRange(1, 8, statusRows + 1, 1).setValues([['状態']].concat(nextRows)); } catch (e) {}
  }
  const status = overallStatus_();
  const updated = Number(props.getProperty('updatedUnix') || 0);
  const updatedText = updated
    ? '掲示板の内容の更新: ' + Utilities.formatDate(new Date(updated * 1000), sheetTimeZone_(), 'yyyy/MM/dd HH:mm')
    : 'まだ公開していません';
  const values = [['SheetBoard の状態'], [status.icon + ' ' + status.text], [status.action || ''], [updatedText]];
  const current = sheet.getRange(1, 10, 4, 1).getValues();
  if (JSON.stringify(current) !== JSON.stringify(values)) {
    try { sheet.getRange(1, 10, 4, 1).setValues(values); } catch (e) {}
  }
}
function page_(bodyHtml, options) {
  options = options || {};
  const closeButton = options.close === false ? '' : '<button class="secondary" onclick="google.script.host.close()">閉じる</button>';
  return '<style>body{font:14px/1.6 "Google Sans","Hiragino Sans",Meiryo,sans-serif;color:#202124;padding:16px}button{border:0;border-radius:6px;padding:10px 18px;min-height:40px;background:#1a73e8;color:white;cursor:pointer;margin:4px}button.secondary{background:#e8eaed;color:#202124}.status{border-left:5px solid #9aa0a6;background:#f1f3f4;padding:12px}.status.ok{border-color:#188038;background:#e6f4ea}.status.warn{border-color:#f9ab00;background:#fef7e0}.status.error{border-color:#d93025;background:#fce8e6}.url{width:75%;padding:8px}table{border-collapse:collapse}td{padding:4px 10px 4px 0;vertical-align:top}</style>' +
    bodyHtml +
    '<script>' + clientHelpers_() + '</script><p id="copied" aria-live="polite"></p>' + closeButton;
}
function buildRangePayload(values, colors, options) {
  options = options || {};
  const extra = options.extra || {};
  const layers = {colors: colors};
  ['styles', 'sizes', 'bg', 'ha', 'wrap', 'chips'].forEach(key => {
    if (Object.prototype.hasOwnProperty.call(extra, key)) layers[key] = extra[key];
  });
  const trimmed = trimEmpty(values, layers);
  const warnings = [];
  if (options.cols !== undefined && String(options.cols).trim() && !parseRatioList(options.cols).length) warnings.push('列幅の比の書き方が違うため無視しました（例: 1,3,1）');
  if (options.align !== undefined && String(options.align).trim() && !parseAlignList(options.align).length) warnings.push('配置の書き方が違うため無視しました（例: l,c,r）');
  const rows = trimmed.values.map(row => row.map(value => value === null || value === undefined ? '' : String(value)));
  const payload = {header: !!options.header, rows: rows};
  if (options.title !== undefined && options.title !== null && String(options.title).trim()) payload.title = String(options.title);
  const cols = Array.isArray(options.cols) ? options.cols : parseRatioList(options.cols);
  const align = Array.isArray(options.align) ? options.align : parseAlignList(options.align);
  if (cols.length) payload.cols = cols;
  if (align.length) payload.align = align;
  if (options.format) {
    const layer = rows.map((row, r) => row.map((_, c) => normalizeColor(trimmed.layers.colors[r][c])));
    if (layer.some(row => row.some(value => value !== ''))) payload.color = layer;
  }
  [['styles', 'style'], ['sizes', 'size'], ['bg', 'bg'], ['ha', 'ha'], ['wrap', 'wrap']].forEach(pair => {
    if (Object.prototype.hasOwnProperty.call(extra, pair[0])) {
      const layer = rows.map((row, r) => row.map((_, c) => trimmed.layers[pair[0]][r][c]));
      const defaults = {styles: 0, bg: '', ha: 'l', wrap: 0};
      const isDefault = pair[1] === 'ha'
        ? layer.every(row => row.every((value, c) => value === (payload.align && payload.align[c] || 'l')))
        : layer.every(row => row.every(value => value === defaults[pair[0]]));
      if (pair[1] === 'size' || !isDefault) payload[pair[1]] = layer;
    }
  });
  if (Object.prototype.hasOwnProperty.call(extra, 'chips')) {
    const layer = rows.map((row, r) => row.map((_, c) => trimmed.layers.chips[r][c]));
    if (layer.some(row => row.some(value => value !== ''))) payload.chip = layer;
  }
  const width = rows.length ? rows[0].length : 0;
  if (Object.prototype.hasOwnProperty.call(extra, 'colw')) payload.colw = extra.colw.slice(0, width);
  if (Object.prototype.hasOwnProperty.call(extra, 'rowh')) payload.rowh = extra.rowh.slice(0, rows.length);
  if (Object.prototype.hasOwnProperty.call(extra, 'merges')) {
    payload.merges = remapMerges(extra.merges, Array(trimmed.values.length).fill(true), Array(trimmed.values[0] ? trimmed.values[0].length : 0).fill(true));
  }
  return {payload: payload, warnings: warnings};
}

function stableStringify(obj) {
  function sorted(value) {
    if (Array.isArray(value)) return value.map(sorted);
    if (value && typeof value === 'object') {
      const result = {};
      Object.keys(value).sort().forEach(key => { result[key] = sorted(value[key]); });
      return result;
    }
    return value;
  }
  return JSON.stringify(sorted(obj));
}

function isoWithOffset(date, offsetMinutes) {
  const shifted = new Date(date.getTime() + offsetMinutes * 60000);
  const pad = n => String(n).padStart(2, '0');
  const sign = offsetMinutes < 0 ? '-' : '+';
  const offset = Math.abs(offsetMinutes);
  return shifted.getUTCFullYear() + '-' + pad(shifted.getUTCMonth() + 1) + '-' + pad(shifted.getUTCDate()) +
    'T' + pad(shifted.getUTCHours()) + ':' + pad(shifted.getUTCMinutes()) + ':' + pad(shifted.getUTCSeconds()) +
    sign + pad(Math.floor(offset / 60)) + ':' + pad(offset % 60);
}

function computeRev_(ranges) {
  // Range order selects the default board; preserve it while normalizing each payload's keys.
  const ordered = Object.keys(ranges).map(name => [name, ranges[name]]);
  return Utilities.computeDigest(Utilities.DigestAlgorithm.MD5, stableStringify(ordered), Utilities.Charset.UTF_8)
    .map(b => ('0' + (b & 255).toString(16)).slice(-2)).join('').slice(0, 8);
}

function onOpen() {
  const admin = SpreadsheetApp.getUi().createMenu('管理者用')
    .addItem('GitHub連携をやり直す', 'reconnectGitHub')
    .addItem('公開先を作り直す', 'recreateDestination')
    .addItem('連携を解除', 'disconnectSheetBoard');
  SpreadsheetApp.getUi().createMenu('SheetBoard')
    .addItem('はじめにセットアップ', 'startSetup')
    .addItem('選択範囲を掲示板に追加', 'addSelectionToBoard')
    .addItem('今すぐ公開', 'publishNow')
    .addItem('状態とURLを表示', 'showStatus')
    .addSubMenu(admin).addToUi();
}
function ensureSettingsSheet_() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  if (ss.getSheetByName(SETTINGS_SHEET)) return;
  const source = ss.getSheets()[0];
  const sheet = ss.insertSheet(SETTINGS_SHEET);
  const name = source.getName() + '!A1:F20';
  sheet.getRange(2, 2, sheet.getMaxRows() - 1, 1).setNumberFormat('@');
  sheet.getRange(2, 4, sheet.getMaxRows() - 1, 2).insertCheckboxes();
  sheet.getRange(1, 1, 2, 7).setValues([
    ['名前', '範囲', 'タイトル', '1行目は見出し', '書式を反映', '列幅の比', '配置'],
    ['#sample', name, '', false, true, '', '']
  ]);
  sheet.setFrozenRows(1);
  const notes = [
    'ワールドの掲示板で選ぶ名前。英数字がおすすめ。#で始めると公開されません。例: rotation',
    '表示するセル範囲。シート名!開始セル:終了セル。例: ローテーション表!A1:F19',
    '掲示板の上に出す見出し。空でもかまいません。例: 今週のローテーション',
    '1行目を色の帯で強調します。シートで見出しに色を付けているなら不要です。例: ☑',
    'シートの色・太字・結合・列幅などを再現します。例: ☑',
    '列幅を手で決めたいときだけ入力。例: 1,3,1',
    '列ごとの寄せを指定。l=左、c=中央、r=右。例: l,c,r'
  ];
  for (let c = 1; c <= 7; c++) sheet.getRange(1, c).setNote(notes[c - 1]);
  sheet.setColumnWidth(8, 300);
  sheet.setColumnWidth(10, 420);
}
function startSetup() {
  ensureSettingsSheet_();
  const user = PropertiesService.getUserProperties();
  const props = PropertiesService.getDocumentProperties();
  if (props.getProperty('gistId') && !user.getProperty('token')) {
    const owner = escapeHtml_(props.getProperty('gistOwner') || '未設定');
    showHtml_('セットアップ', '<p>このシートは <b>' + owner + '</b> さんのGitHubで、すでにセットアップされています。スタッフの方は操作不要です。シートを編集するだけで掲示板に反映されます。</p>' +
      '<button onclick="google.script.host.close()">閉じる</button><button class="secondary" onclick="startSetupConfirmed(this)">管理者を引き継ぐので続ける</button>' +
      '<script>function startSetupConfirmed(b){busy(b);google.script.run.withFailureHandler(fail).startSetupConfirmed()}</script>', {close: false});
    return;
  }
  if (user.getProperty('token') && user.getProperty('reauthRequired') !== 'true') showFinishDialog_();
  else beginAuthorization_(true);
}
function startSetupConfirmed() { beginAuthorization_(true); }
function reconnectGitHub() { beginAuthorization_(false); }

function beginAuthorization_(setup) {
  const authEpoch = advanceAuthEpoch_();
  if (!GITHUB_CLIENT_ID) {
    const saved = setup === true ? 'finish()' : setup === 'handoff'
      ? 'document.getElementById("status").textContent="連携しました。メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を実行してください。"'
      : 'document.getElementById("status").textContent="連携しました。"';
    showHtml_('GitHub連携', '<div id="setupFlow"><p>掲示板を自動で更新するために、あなたのGitHubアカウントと連携します。</p>' +
      '<p>GitHubで作成した、gist権限のあるPersonal access token（classic）を貼り付けてください。</p>' +
      '<input id="token" type="password" style="width:95%"><p><button onclick="save()">連携する</button></p>' +
      '<p id="status"></p></div><div id="result" style="display:none"></div>' +
      '<script>function save(){var b=event.currentTarget;busy(b);document.getElementById("status").textContent="確認しています…";' +
      'google.script.run.withSuccessHandler(function(r){if(r&&r.handoff){document.getElementById("status").textContent="連携しました。メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を実行してください。"}else{' + saved + '}' +
      '}).withFailureHandler(fail).saveToken(document.getElementById("token").value, true)}' + '</script>');
    return;
  }
  try {
    const response = githubForm_('https://github.com/login/device/code', {client_id: GITHUB_CLIENT_ID, scope: 'gist'});
    if (!response.device_code || !response.user_code || !/^https:\/\/github\.com\/login\/device\/?$/.test(response.verification_uri || '')) throw new Error('連携を開始できませんでした。');
    const user = PropertiesService.getUserProperties();
    const lock = LockService.getUserLock();
     if (!lock.tryLock(10000)) throw makeError_('GitHub連携の開始が混み合っています。少し待ってから、もう一度お試しください。', 503);
    try {
      if (Number(user.getProperty('authEpoch') || 0) !== authEpoch)
        throw makeError_('別のGitHub連携操作が始まったため、認証を中断しました。もう一度お試しください。', 401, 'reconnect');
      user.setProperty('deviceCode', response.device_code);
    }
    finally { lock.releaseLock(); }
    const interval = Math.max(1, Number(response.interval) || 5);
    const code = escapeHtml_(response.user_code);
    const uri = escapeHtml_(response.verification_uri);
    showHtml_('GitHub連携', '<div id="setupFlow"><p>掲示板を自動で更新するために、あなたのGitHubアカウントと連携します。</p>' +
      '<ol><li>「コードをコピー」を押す</li><li>「GitHubを開く」を押す（GitHubにログインしていなければログイン）</li>' +
      '<li>コードを貼り付けて「Continue」→「Authorize」</li><li><b>この画面に戻ってください。閉じないでください。</b>承認を確認すると自動で次に進みます。</li></ol>' +
      '<div id="code" style="font-size:38px;font-weight:bold">' + code + '</div>' +
      '<p><a href="' + uri + '" target="_blank" rel="noopener">GitHubを開く</a> <button onclick="copyCode()">コードをコピー</button></p>' +
      '<p id="status">GitHubでの承認を待っています…</p></div><div id="result" style="display:none"></div><script>let retries=0,delay=' + interval * 1000 + ';' +
      'function copyCode(){navigator.clipboard.writeText(document.getElementById("code").textContent).then(function(){document.getElementById("status").textContent="コピーしました";setTimeout(function(){document.getElementById("status").textContent="GitHubでの承認を待っています…"},2000)})}' +
      'function poll(){google.script.run.withSuccessHandler(function(r){retries=0;if(r.status==="success"){' +
      '(r.handoff?' + 'document.getElementById("status").textContent="連携しました。メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を実行してください。"' + ':' +
      (setup === true ? 'finish()' : setup === 'handoff' ? 'document.getElementById("status").textContent="連携しました。メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を実行してください。"' : 'document.getElementById("status").textContent="連携しました。"') + ')'+
      '}else if(r.status==="pending"){if(r.slow)delay+=5000;setTimeout(poll,delay)}else{document.getElementById("status").textContent=r.message}})' +
      '.withFailureHandler(function(e){if(retries++<3)setTimeout(poll,delay);else fail(e)}).pollDeviceToken()}setTimeout(poll,delay);' + '</script>');
  } catch (e) { showMessage_('GitHub連携', errorText_(e)); }
}

function clientHelpers_() {
  return 'function fail(e){var s=document.getElementById("status");if(s)s.textContent=(e&&e.message)||"処理できませんでした。";var b=document.querySelector("button[disabled]");if(b)b.disabled=false}' +
    'function busy(button){if(button)button.disabled=true}' +
    'function beginHandoff(b){busy(b);google.script.run.withSuccessHandler(function(){var s=document.getElementById("status");if(s)s.textContent="GitHubと連携しました。メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を実行してください。"}).withFailureHandler(function(e){b.disabled=false;fail(e)}).beginHandoffAuthorization()}' +
    'function copyText(id){var e=document.getElementById(id);navigator.clipboard.writeText(e.value||e.textContent).then(function(){var m=document.getElementById("copied");if(m){m.textContent="コピーしました";setTimeout(function(){m.textContent=""},2000)}})}' +
    'function finish(){var b=event&&event.currentTarget;busy(b);var s=document.getElementById("status");if(s)s.textContent="セットアップを進めています…";' +
    'google.script.run.withSuccessHandler(function(r){var x=document.getElementById("result");var f=document.getElementById("setupFlow");if(f)f.style.display="none";if(x){x.style.display="block";' +
    'x.innerHTML="✔ セットアップが完了しました<p>掲示板に出るのは、設定タブに書いた範囲だけです。</p><input id=url class=url readonly><button id=copyurl>コピー</button>"+' +
    '(r.rangeCount?"<p>公開しました（"+r.rangeCount+" 個の範囲）</p>":"<h3>次にやること</h3><ol><li>掲示板に出したい表のタブを開き、表をドラッグで選択</li>"+' +
    '"<li>メニュー「SheetBoard → 選択範囲を掲示板に追加」（または設定タブのB列に範囲を書き、A列の先頭の # を消す）</li>"+' +
    '"<li>数分以内に掲示板に反映されます</li></ol>");document.getElementById("copyurl").onclick=function(){copyText("url")};' +
    'document.getElementById("url").value=r.url;}if(s)s.textContent=""}).withFailureHandler(fail).finishSetup()}';
}
function showFinishDialog_() {
  showHtml_('セットアップ', '<div id="setupFlow"><p>設定タブを準備して、掲示板用のURLを作ります。</p>' +
    '<p id="status"></p><button onclick="finish()">セットアップを続ける</button></div>' +
    '<div id="result" style="display:none"></div><script></script>');
}
function githubForm_(url, data) {
  let response;
  try { response = UrlFetchApp.fetch(url, {method: 'post', payload: data, headers: {Accept: 'application/json'}, muteHttpExceptions: true}); }
  catch (e) { throw makeError_('GitHubに接続できませんでした。少し待ってから、もう一度お試しください。', 503, '', true); }
  const status = response.getResponseCode();
  if (status < 200 || status >= 300) throw makeError_('GitHubへの接続に失敗しました（' + status + '）。', status, '', true);
  try { return JSON.parse(response.getContentText()); }
  catch (e) { throw makeError_('GitHubから正しい応答がありませんでした。もう一度お試しください。', 502, '', true); }
}

function pollDeviceToken() {
  const user = PropertiesService.getUserProperties();
  const code = user.getProperty('deviceCode');
  if (!code) return {status: 'error', message: 'コードが見つかりません。この画面を閉じて、同じメニューをもう一度実行してください。'};
  const result = githubForm_('https://github.com/login/oauth/access_token', {client_id: GITHUB_CLIENT_ID, device_code: code, grant_type: 'urn:ietf:params:oauth:grant-type:device_code'});
  if (result.access_token) {
    const saved = saveToken_(result.access_token, false, result, code);
    return {status: 'success', handoff: saved.handoff};
  }
  if (result.error === 'authorization_pending' || result.error === 'slow_down') return {status: 'pending', slow: result.error === 'slow_down'};
  deleteDeviceCode_(code);
  const message = result.error === 'expired_token' ? 'コードの有効期限（15分）が切れました。' :
    result.error === 'access_denied' ? 'GitHubで承認がキャンセルされました。' : '連携できませんでした（' + String(result.error || 'unknown') + '）。';
  return {status: 'error', message: message + 'この画面を閉じて、同じメニューをもう一度実行してください。'};
}

function saveToken(token, checkScopes) { return saveToken_(token, checkScopes); }
function saveToken_(token, checkScopes, oauthResponse, expectedDeviceCode) {
  const user = PropertiesService.getUserProperties();
  const expectedAuthEpoch = Number(user.getProperty('authEpoch') || 0);
  token = String(token || '').trim();
  if (!token) throw new Error('トークンを入力してください。');
  let profile;
  try { profile = githubRequest_('get', '/user', null, token); }
  catch (e) { throw makeError_('GitHubのアカウントを確認できませんでした（' + (e.status || '接続エラー') + '）。', e.status); }
  if (!profile.body.login) throw new Error('GitHubのアカウントを確認できませんでした（' + profile.status + '）。');
  if (checkScopes && !String(profile.scopes || '').split(',').some(s => s.trim() === 'gist')) throw new Error('gist権限のあるPersonal access token（classic）を入力してください。');
  const lock = LockService.getUserLock();
     if (!lock.tryLock(10000)) throw makeError_('GitHub連携の保存が混み合っています。少し待ってから、もう一度お試しください。', 503);
  try {
    if (Number(user.getProperty('authEpoch') || 0) !== expectedAuthEpoch)
      throw makeError_('GitHub連携が解除または更新されました。もう一度連携してください。', 401, 'reconnect');
    if (expectedDeviceCode && user.getProperty('deviceCode') !== expectedDeviceCode)
      throw makeError_('GitHub連携が解除または更新されました。もう一度連携してください。', 401, 'reconnect');
    const values = {token: token, login: profile.body.login};
    ['authType', 'refreshToken', 'tokenExpiresAt', 'refreshTokenExpiresAt', 'reauthRequired'].forEach(key => user.deleteProperty(key));
    if (oauthResponse) {
      const expiresIn = positiveSeconds_(oauthResponse.expires_in);
      const refreshExpiresIn = positiveSeconds_(oauthResponse.refresh_token_expires_in);
      values.authType = 'oauth';
      if (oauthResponse.refresh_token) values.refreshToken = oauthResponse.refresh_token;
      if (expiresIn) values.tokenExpiresAt = String(Date.now() + expiresIn * 1000);
      if (refreshExpiresIn) values.refreshTokenExpiresAt = String(Date.now() + refreshExpiresIn * 1000);
    }
    user.setProperties(values);
    user.deleteProperty('deviceCode');
  } finally { lock.releaseLock(); }
  const doc = PropertiesService.getDocumentProperties();
  if (doc.getProperty('gistId') && doc.getProperty('gistOwner') && !isGistOwner_()) return {handoff: true};
  ensureTriggers_();
  if (Number(user.getProperty('authEpoch') || 0) !== expectedAuthEpoch && !user.getProperty('token')) removeTriggers_();
  return {handoff: false};
}

function positiveSeconds_(value) {
  const seconds = Number(value);
  return Number.isFinite(seconds) && seconds > 0 ? seconds : 0;
}

function advanceAuthEpoch_() {
  const user = PropertiesService.getUserProperties();
  const lock = LockService.getUserLock();
   if (!lock.tryLock(10000)) throw makeError_('GitHub連携の開始が混み合っています。少し待ってから、もう一度お試しください。', 503);
  try {
    const epoch = Number(user.getProperty('authEpoch') || 0) + 1;
    user.setProperty('authEpoch', String(epoch));
    return epoch;
  } finally { lock.releaseLock(); }
}

function deleteDeviceCode_(expectedDeviceCode) {
  const user = PropertiesService.getUserProperties();
  const lock = LockService.getUserLock();
  if (!lock.tryLock(10000)) return false;
  try {
    if (user.getProperty('deviceCode') !== expectedDeviceCode) return false;
    user.deleteProperty('deviceCode');
    return true;
  } finally { lock.releaseLock(); }
}

function refreshAccessError_(user, result, status) {
  if (result.error === 'bad_refresh_token' || result.error === 'invalid_grant' || status === 401) {
    user.setProperty('reauthRequired', 'true');
    return makeError_(reconnectMessage_(false), 401, 'reconnect');
  }
  if (status === 403 || result.error === 'unauthorized_client') {
    return makeError_('GitHub連携の設定に問題があります。配布者に問い合わせてください（エラー番号 ' + status + '）。', 403, 'permission');
  }
  return null;
}

function refreshAccessToken_(force, observedToken) {
  const user = PropertiesService.getUserProperties();
  if (!user.getProperty('token')) return null;
  const lock = LockService.getUserLock();
  if (!lock.tryLock(10000)) throw makeError_('GitHub連携を更新できませんでした。自動で再試行します。操作は不要です（エラー番号 503）。', 503);
  try {
    const token = user.getProperty('token');
    const expiresAt = Number(user.getProperty('tokenExpiresAt') || 0);
    if (user.getProperty('reauthRequired') === 'true') throw makeError_(reconnectMessage_(false), 401, 'reconnect');
    if (observedToken && token !== observedToken) return token;
    if (!force && (!expiresAt || expiresAt > Date.now() + TOKEN_REFRESH_MARGIN_MS)) return token;
    const refreshToken = user.getProperty('refreshToken');
    if (!refreshToken) {
      if (!force && expiresAt && expiresAt <= Date.now() + TOKEN_REFRESH_MARGIN_MS) {
        user.setProperty('reauthRequired', 'true');
        throw makeError_(reconnectMessage_(false), 401, 'reconnect');
      }
      if (force) {
        user.setProperty('reauthRequired', 'true');
        throw makeError_(reconnectMessage_(false), 401, 'reconnect');
      }
      return token;
    }
    const refreshExpiresAt = Number(user.getProperty('refreshTokenExpiresAt') || 0);
    if (refreshExpiresAt && refreshExpiresAt <= Date.now()) {
      user.setProperty('reauthRequired', 'true');
      throw makeError_(reconnectMessage_(false), 401, 'reconnect');
    }
    let response;
    try {
      response = UrlFetchApp.fetch('https://github.com/login/oauth/access_token', {
        method: 'post',
        payload: {client_id: GITHUB_CLIENT_ID, grant_type: 'refresh_token', refresh_token: refreshToken},
        headers: {Accept: 'application/json'},
        muteHttpExceptions: true
      });
    } catch (e) {
      throw makeError_('GitHub連携を更新できませんでした。自動で再試行します。操作は不要です（エラー番号 503）。', 503, 'temporary', true);
    }
    const status = response.getResponseCode();
    let result;
    try { result = JSON.parse(response.getContentText() || '{}'); }
    catch (e) { throw makeError_('GitHubから正しい応答がありませんでした。自動で再試行します。操作は不要です。', 502, 'temporary', true); }
    const mappedError = refreshAccessError_(user, result, status);
    if (status < 200 || status >= 300) {
      if (mappedError) throw mappedError;
      throw makeError_('GitHub連携を更新できませんでした。自動で再試行します。操作は不要です（エラー番号 ' + status + '）。', status || 503, 'temporary', true);
    }
    if (result.access_token && !result.refresh_token && !positiveSeconds_(result.expires_in) && !positiveSeconds_(result.refresh_token_expires_in)) {
      user.setProperties({token: result.access_token, authType: 'oauth'});
      ['refreshToken', 'tokenExpiresAt', 'refreshTokenExpiresAt', 'reauthRequired'].forEach(key => user.deleteProperty(key));
      return result.access_token;
    }
    if (result.access_token && result.refresh_token && positiveSeconds_(result.expires_in) && positiveSeconds_(result.refresh_token_expires_in)) {
      const now = Date.now();
      const values = {
        token: result.access_token,
        refreshToken: result.refresh_token,
        authType: 'oauth',
        tokenExpiresAt: String(now + Number(result.expires_in) * 1000),
        refreshTokenExpiresAt: String(now + Number(result.refresh_token_expires_in) * 1000)
      };
      user.setProperties(values);
      user.deleteProperty('reauthRequired');
      return result.access_token;
    }
    if (mappedError) throw mappedError;
    if (result.access_token || result.refresh_token) {
      user.setProperty('reauthRequired', 'true');
      throw makeError_(reconnectMessage_(false), 401, 'reconnect');
    }
    throw makeError_('GitHub連携を更新できませんでした。自動で再試行します。操作は不要です（エラー番号 ' + status + '）。', status || 503, 'temporary', true);
  } finally { lock.releaseLock(); }
}

function githubRequest_(method, path, body, token) {
  const explicitToken = !!token;
  if (!explicitToken) token = refreshAccessToken_(false);
  if (!token) throw makeError_(reconnectMessage_(false), 401, 'reconnect');
  function send(accessToken) {
    const options = {method: method, headers: {Authorization: 'Bearer ' + accessToken, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'}, muteHttpExceptions: true};
    if (body !== null && body !== undefined) { options.contentType = 'application/json'; options.payload = JSON.stringify(body); }
    try { return UrlFetchApp.fetch('https://api.github.com' + path, options); }
    catch (e) { throw makeError_('GitHubに接続できませんでした。自動で再試行します。操作は不要です。', 503, 'temporary', true); }
  }
  let response = send(token);
  if (response.getResponseCode() === 401 && !explicitToken) {
    token = refreshAccessToken_(true, token);
    if (!token) throw makeError_(reconnectMessage_(false), 401, 'reconnect');
    response = send(token);
  }
  const status = response.getResponseCode();
  if (status < 200 || status >= 300) {
    const reauth = status === 401 && !explicitToken;
    if (reauth) markReauthRequired_(token);
    throw makeError_(reauth ? reconnectMessage_(false) : status === 403 ? permissionMessage_() : 'GitHubへの公開に失敗しました（エラー番号 ' + status + '）。', status, reauth ? 'reconnect' : '', true);
  }
  const headers = response.getAllHeaders();
  const scopeKey = Object.keys(headers).find(k => k.toLowerCase() === 'x-oauth-scopes');
  return {status: status, body: JSON.parse(response.getContentText() || '{}'), scopes: scopeKey ? headers[scopeKey] : ''};
}

function markReauthRequired_(observedToken) {
  const user = PropertiesService.getUserProperties();
  const lock = LockService.getUserLock();
  if (!lock.tryLock(10000)) return false;
  try {
    if (user.getProperty('token') !== observedToken) return false;
    user.setProperty('reauthRequired', 'true');
    return true;
  } finally { lock.releaseLock(); }
}

function makeError_(message, status, authAction, githubOrigin) {
  const error = new Error(message);
  error.status = status;
  if (authAction) error.authAction = authAction;
  if (githubOrigin) error.githubOrigin = true;
  return error;
}
function errorText_(e) {
  const message = e && e.message ? String(e.message) : '処理できませんでした。';
  const code = e && e.status ? String(e.status) : '';
  if (/unauthorized_client/i.test(message)) return 'GitHub連携の設定に問題があります。配布者に問い合わせてください（エラー番号 ' + (code || '不明') + '）。';
  if (code === '403') return permissionMessage_() + '（エラー番号 403）';
  if (code === '401' || (e && e.authAction === 'reconnect')) return reconnectMessage_(true);
  if (/掲示板に出す内容が多すぎます/.test(message)) return '掲示板に出す内容が多すぎます。設定タブの範囲を小さくするか、いくつかの行に分けてください。';
  if (code && Number(code) >= 500) {
    if (e.authAction === 'temporary') return message;
    if (e.githubOrigin) return 'GitHubが一時的に応答していません。自動で再試行します。操作は不要です（エラー番号 ' + code + '）。';
    return 'GitHubに公開できませんでした（エラー番号 ' + code + '）。メニュー「SheetBoard → 状態とURLを表示」の内容を配布者に伝えてください。';
  }
  if (/アカウントを確認できませんでした/.test(message)) return 'GitHubのアカウントを確認できませんでした。もう一度お試しください（エラー番号 ' + (code || '不明') + '）。';
  if (code) return 'GitHubに公開できませんでした（エラー番号 ' + code + '）。しばらくしても直らない場合は、メニュー「SheetBoard → 状態とURLを表示」の内容を配布者に伝えてください。';
  if (!message.trim() || /^[\x00-\x7F\s]+$/.test(message)) return '処理できませんでした。しばらくしても直らない場合は、メニュー「SheetBoard → 状態とURLを表示」の内容を配布者に伝えてください。';
  return message;
}
function createGist_(content, token) {
  const user = PropertiesService.getUserProperties();
  let login = user.getProperty('login');
  if (!login) {
    let profile;
    try { profile = githubRequest_('get', '/user', null, token); }
    catch (e) { throw makeError_('GitHubのアカウントを確認できませんでした（' + (e.status || '接続エラー') + '）。', e.status); }
    login = profile.body.login;
    if (!login) throw new Error('GitHubのアカウントを確認できませんでした（' + profile.status + '）。');
    user.setProperty('login', login);
  }
  const files = {}; files[GIST_FILENAME] = {content: content};
  const gist = githubRequest_('post', '/gists', {description: 'Sheet Board: ' + SpreadsheetApp.getActiveSpreadsheet().getName(), public: false, files: files}, token).body;
  if (!gist.id) throw new Error('公開先を作成できませんでした。');
  const props = PropertiesService.getDocumentProperties();
  props.setProperties({gistId: gist.id, gistOwner: login, worldUrl: makeWorldUrl_(login, gist.id)});
  return gist.id;
}

function worldUrl_() {
  return PropertiesService.getDocumentProperties().getProperty('worldUrl') || '';
}
function makeWorldUrl_(login, id) { return 'https://gist.githubusercontent.com/' + encodeURIComponent(login) + '/' + encodeURIComponent(id) + '/raw/' + GIST_FILENAME; }

function isGistOwner_() {
  const props = PropertiesService.getDocumentProperties();
  const owner = props.getProperty('gistId') && props.getProperty('gistOwner');
  if (!owner) return true;
  const login = PropertiesService.getUserProperties().getProperty('login');
  if (!login) throw new Error('GitHubのアカウントを確認できませんでした。GitHub連携をやり直してください。');
  return owner.toLowerCase() === login.toLowerCase();
}
function checkGistOwner_() {
  const owner = PropertiesService.getDocumentProperties().getProperty('gistOwner');
  if (!isGistOwner_()) throw new Error(ownerMismatchMessage_(owner));
}
function ownerMismatchMessage_(owner) {
  return 'この公開先は ' + owner + ' さんのGitHubにあります。管理者を交代する場合は、メニュー「SheetBoard → 管理者用 → 公開先を作り直す」を使ってください。';
}

function finishSetup() {
  ensureSettingsSheet_();
  if (!PropertiesService.getUserProperties().getProperty('token')) throw new Error('先にGitHubを連携してください。');
  checkGistOwner_();
  installTriggers_();
  const result = publish_(true);
  if (!result) throw new Error(publishFailureMessage_());
  PropertiesService.getDocumentProperties().deleteProperty('urlChangeNotice');
  writeSheetStatus_();
  try { SpreadsheetApp.getActiveSpreadsheet().setActiveSheet(SpreadsheetApp.getActiveSpreadsheet().getSheetByName(SETTINGS_SHEET)); } catch (e) {}
  return {url: worldUrl_(), rangeCount: publishedNameCount_(PropertiesService.getDocumentProperties())};
}
function publishFailureMessage_() {
  const outcome = PropertiesService.getDocumentProperties().getProperty('lastPublishOutcome');
  return outcome === 'busy' ? 'ほかの公開処理が進行中です。このあと自動で公開されるので、操作は不要です。' :
    outcome === 'no_token' ? '受け付けました。数分以内に自動で公開されます。操作は不要です。' :
    outcome === 'reauth_required' ? reconnectMessage_(true) :
    '公開できませんでした。メニュー「SheetBoard → 状態とURLを表示」で原因を確認してください。';
}
function installTriggers_() {
  removeTriggers_();
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  ScriptApp.newTrigger('onEditInstalled').forSpreadsheet(ss).onEdit().create();
  ScriptApp.newTrigger('onChangeInstalled').forSpreadsheet(ss).onChange().create();
  ScriptApp.newTrigger('onTimer').timeBased().everyMinutes(5).create();
}
function ensureTriggers_() {
  const handlers = ScriptApp.getProjectTriggers().map(t => t.getHandlerFunction());
  if (!['onEditInstalled', 'onChangeInstalled', 'onTimer'].every(h => handlers.includes(h))) {
    checkGistOwner_();
    installTriggers_();
  }
}

function removeTriggers_() {
  ScriptApp.getProjectTriggers().forEach(t => {
    if (t.getHandlerFunction() === 'onEditInstalled' || t.getHandlerFunction() === 'onChangeInstalled' || t.getHandlerFunction() === 'onTimer') ScriptApp.deleteTrigger(t);
  });
}

// Trigger handlers swallow errors: publish_() already records them and mails once a day, and a
// thrown error would add Google's own failure mail for every run on top of that.
function onEditInstalled(e) {
  if (e && e.range && e.range.getSheet && e.range.getSheet().getName() === SETTINGS_SHEET && e.range.getColumn() >= 8) return;
  const last = Number(PropertiesService.getDocumentProperties().getProperty('lastTriggerMillis') || 0);
  if (Date.now() - last < 5000) { setPending_(); return; }
  try { publish_(false, true); } catch (e) {}
}
function onChangeInstalled(e) {
  if (!e || !['FORMAT', 'INSERT_ROW', 'REMOVE_ROW', 'INSERT_COLUMN', 'REMOVE_COLUMN', 'OTHER'].includes(e.changeType)) return;
  const last = Number(PropertiesService.getDocumentProperties().getProperty('lastTriggerMillis') || 0);
  if (Date.now() - last < 5000) { setPending_(); return; }
  try { publish_(false, true); } catch (e) {}
}
function onTimer() { try { publish_(false, true); } catch (e) {} }
function setPending_() {
  const props = PropertiesService.getDocumentProperties();
  props.setProperty('pending', Utilities.getUuid());
  writeSheetStatus_();
}
function addSelectionToBoard() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const selected = ss.getActiveRange();
  const active = ss.getActiveSheet();
  if (!selected || (active && active.getName() === SETTINGS_SHEET) ||
      (selected.getNumRows() === 1 && selected.getNumColumns() === 1)) {
    showMessage_('掲示板に追加', '掲示板に出したい表のタブを開き、表全体をドラッグで選択してから、メニュー「SheetBoard → 選択範囲を掲示板に追加」をもう一度実行してください。');
    return;
  }
  ensureSettingsSheet_();
  const settings = ss.getSheetByName(SETTINGS_SHEET);
  const source = active || selected.getSheet();
  const range = source.getName() + '!' + selected.getA1Notation();
  let rows = [];
  const last = Math.max(2, settings.getLastRow());
  try {
    rows = settings.getRange(2, 1, last - 1, 7).getValues();
  } catch (e) {
    showMessage_('掲示板に追加', '設定タブを読み取れませんでした。管理者に範囲を追加するよう伝えてください: ' + range);
    return;
  }
  let target = 0;
  const initial = ss.getSheets()[0].getName() + '!A1:F20';
  for (let i = 0; i < rows.length; i++) {
    if (String(rows[i][0] || '').trim() === '#sample' && String(rows[i][1] || '').trim() === initial) {
      target = i + 2;
      break;
    }
  }
  const used = Object.create(null);
  rows.forEach(row => {
    const name = String(row[0] || '').trim();
    if (name) used[name] = true;
  });
  let number = 1;
  while (used['board' + number]) number++;
  if (!target) {
    const empty = rows.findIndex(row => !String(row[0] || '').trim() && !String(row[1] || '').trim());
    target = empty >= 0 ? empty + 2 : Math.max(2, settings.getLastRow() + 1);
  }
  try {
    settings.getRange(target, 1, 1, 5).setValues([['board' + number, range, '', false, true]]);
  } catch (e) {
    showMessage_('掲示板に追加', '設定タブは管理者だけが編集できます。管理者に、次の範囲を追加するよう伝えてください: ' + range);
    return;
  }
  let addMessage;
  if (PropertiesService.getUserProperties().getProperty('token')) {
    try {
      if (publish_(false)) {
        addMessage = '✔ 追加しました。公開しました。';
      } else {
        addMessage = '✔ 追加しました。自動公開を待っています。';
      }
    } catch (e) {
      showMessage_('掲示板に追加', '公開に失敗しました: ' + errorText_(e));
      return;
    }
  } else {
    const props = PropertiesService.getDocumentProperties();
    const rowStatus = parseJsonArray_(props.getProperty('rowStatus'));
    rowStatus[target - 2] = '⏳ 管理者の自動公開を待っています';
    props.setProperty('rowStatus', JSON.stringify(boundedStatusArray_(rowStatus)));
    setPending_();
    addMessage = '✔ 追加しました。自動公開を待っています。';
  }
  writeSheetStatus_();
  try {
    ss.setActiveSheet(settings);
  } catch (e) {}
  showMessage_('掲示板に追加', addMessage + '\n名前: board' + number + '\n範囲: ' + range +
    '\n数分以内に掲示板に反映されます。\nワールドに掲示板が複数ある場合は、ワールド作者に名前「board' + number + '」を伝えてください。');
}
function publishNow() {
  try {
    if (!PropertiesService.getUserProperties().getProperty('token')) {
      setPending_();
      showMessage_('公開', '受け付けました。数分以内に自動で公開されます。操作は不要です。');
      return;
    }
    if (publish_(true)) {
      const props = PropertiesService.getDocumentProperties();
      const names = JSON.parse(props.getProperty('publishedNames') || '[]');
      const nameCount = publishedNameCount_(props);
      const status = overallStatus_();
      const warnings = JSON.parse(props.getProperty('warnings') || '[]');
      const message = nameCount
        ? '✔ 公開しました。ワールドの掲示板には1〜2分で反映されます。' +
          (warnings.length ? '\n\n次の行には注意があります:\n・' + warnings.join('\n・') : '')
        : status.text + '\n' + status.action;
      showMessage_('公開', message);
    } else {
      const outcome = PropertiesService.getDocumentProperties().getProperty('lastPublishOutcome');
      const message = publishFailureMessage_();
      showMessage_('公開', message);
    }
  } catch (e) {
    showMessage_('公開できませんでした', errorText_(e));
  }
}
function readRanges_(startedAt) {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const settings = ss.getSheetByName(SETTINGS_SHEET);
  if (!settings) throw new Error('設定タブがありません。はじめにセットアップを実行してください。');
  const config = settings.getLastRow() > 1 ? settings.getRange(2, 1, settings.getLastRow() - 1, 7).getValues() : [];
  const ranges = Object.create(null);
  const seen = Object.create(null);
  const chipSourceCache = Object.create(null);
  const filterCache = Object.create(null);
  const warnings = [];
  const rowStatus = [];
  config.forEach((row, index) => {
    checkExecutionBudget_(startedAt);
    const rowWarnings = [];
    const name = String(row[0] === null ? '' : row[0]).trim();
    if (!name) {
      rowStatus[index] = String(row[1] || '').trim() ? '⚠ 名前（A列）が空のため公開されません' : '';
      return;
    }
    if (name.startsWith('#')) {
      rowStatus[index] = '非公開（名前の先頭が # のため）';
      return;
    }
    if (Object.prototype.hasOwnProperty.call(seen, name)) {
      warnings.push('名前「' + name + '」が重複しています。先の行を使います。');
      rowStatus[index] = '⚠ 同じ名前が上の行にあるため使われません';
      return;
    }
    seen[name] = true;
    try {
      const ref = parseRangeRef(row[1]);
      if (!ref || !ref.a1) throw new Error('範囲に文字が入っていません');
      const sheet = ref.sheet ? ss.getSheetByName(ref.sheet) : ss.getSheets().find(s => s.getName() !== SETTINGS_SHEET);
      if (!sheet) throw new Error('シート「' + ref.sheet + '」が見つかりません。タブ名を確認してください');
      let range;
      try { range = sheet.getRange(ref.a1); }
      catch (e) { throw new Error('範囲「' + ref.a1 + '」を読めません。'); }
      const startRow = range.getRow();
      const startCol = range.getColumn();
      const rows = Math.min(range.getNumRows(), Math.max(0, sheet.getLastRow() - startRow + 1));
      const cols = Math.min(range.getNumColumns(), Math.max(0, sheet.getLastColumn() - startCol + 1));
      if (!rows || !cols) {
        const emptyResult = buildRangePayload([], [], {
          title: row[2],
          header: row[3] === true,
          format: row[4] === true,
          cols: row[5],
          align: row[6]
        });
        ranges[name] = emptyResult.payload;
        emptyResult.warnings.forEach(warning => {
          warnings.push('「' + name + '」: ' + warning);
          rowWarnings.push(warning);
        });
        rowStatus[index] = '⚠ 範囲に文字が入っていません' +
          (rowWarnings.length ? ' ／ ' + rowWarnings.join(' ／ ') : '');
        return;
      }
      range = sheet.getRange(startRow, startCol, rows, cols);
      const sheetName = sheet.getName();
      if (!Object.prototype.hasOwnProperty.call(filterCache, sheetName)) {
        filterCache[sheetName] = typeof sheet.getFilter === 'function' ? !!sheet.getFilter() : false;
      }
      const hasFilter = filterCache[sheetName];
      const keepRows = Array.from({length: range.getNumRows()}, (_, i) => {
        checkExecutionBudget_(startedAt);
        return !sheet.isRowHiddenByUser(startRow + i) && (!hasFilter || !sheet.isRowHiddenByFilter(startRow + i));
      });
      const keepCols = Array.from({length: range.getNumColumns()}, (_, i) => {
        checkExecutionBudget_(startedAt);
        return !sheet.isColumnHiddenByUser(startCol + i);
      });
      const validations = range.getDataValidations();
      let values = range.getDisplayValues().map((line, rowIndex) => line.map((value, columnIndex) => {
        const validation = validations[rowIndex][columnIndex];
        if (validation && validation.getCriteriaType() === SpreadsheetApp.DataValidationCriteria.CHECKBOX) {
          if (String(value).toUpperCase() === 'TRUE') return '■';
          if (String(value).toUpperCase() === 'FALSE') return '□';
        }
        return value;
      }));
      const weights = row[4] === true ? range.getFontWeights() : [];
      let colors = [];
      const extra = {};
      if (row[4] === true) {
        extra.chips = values.map((line, r) => line.map((value, c) => {
          const validation = validations[r] && validations[r][c];
          if (!validation) return '';
          const criteria = validation.getCriteriaType();
          if (criteria === SpreadsheetApp.DataValidationCriteria.CHECKBOX) return '';
          if (criteria !== SpreadsheetApp.DataValidationCriteria.VALUE_IN_LIST &&
              criteria !== SpreadsheetApp.DataValidationCriteria.VALUE_IN_RANGE) return '';
          const criteriaValues = validation.getCriteriaValues() || [];
          const showDropdown = criteriaValues[1] !== false;
          let swatch = null;
          if (criteria === SpreadsheetApp.DataValidationCriteria.VALUE_IN_RANGE && criteriaValues[0]) {
            let sourceKey = '';
            try {
              const sourceRange = criteriaValues[0];
              sourceKey = sourceRange.getSheet().getName() + '!' + sourceRange.getA1Notation();
              if (!Object.prototype.hasOwnProperty.call(chipSourceCache, sourceKey)) {
                const sourceSheet = sourceRange.getSheet();
                const sourceRow = sourceRange.getRow();
                const sourceCol = sourceRange.getColumn();
                const sourceRows = Math.min(sourceRange.getNumRows(), Math.max(0, sourceSheet.getLastRow() - sourceRow + 1));
                const sourceCols = Math.min(sourceRange.getNumColumns(), Math.max(0, sourceSheet.getLastColumn() - sourceCol + 1));
                if (!sourceRows || !sourceCols) {
                  chipSourceCache[sourceKey] = {displayValues: [], backgrounds: [], fontColors: []};
                } else {
                  checkExecutionBudget_(startedAt);
                  const boundedSource = sourceSheet.getRange(sourceRow, sourceCol, sourceRows, sourceCols);
                  const fontColors = boundedSource.getFontColorObjects().map(sourceLine => sourceLine.map(color => {
                    try {
                      return color.asRgbColor().asHexString();
                    } catch (e) {
                      return '';
                    }
                  }));
                  chipSourceCache[sourceKey] = {
                    displayValues: boundedSource.getDisplayValues(),
                    backgrounds: boundedSource.getBackgrounds(),
                    fontColors: fontColors
                  };
                }
              }
              const source = chipSourceCache[sourceKey];
              if (source) swatch = chipSwatch(source.displayValues, source.backgrounds, source.fontColors, value);
            } catch (e) {
              if (e && e.executionBudgetExceeded) throw e;
              if (sourceKey) chipSourceCache[sourceKey] = null;
            }
          }
          return chipCode(value, showDropdown, swatch);
        }));
        colors = range.getFontColorObjects().map(line => line.map(color => {
          try {
            return color.asRgbColor().asHexString();
          } catch (e) {
            return '';
          }
        }));
        const fontStyles = range.getFontStyles();
        const fontLines = range.getFontLines();
        extra.styles = weights.map((line, r) => line.map((weight, c) => styleBits(weight, fontStyles[r][c], fontLines[r][c])));
        extra.sizes = range.getFontSizes();
        extra.bg = range.getBackgrounds().map(line => line.map(normalizeBackground_));
        if (row[6] === null || row[6] === undefined || String(row[6]).trim() === '') {
          extra.ha = range.getHorizontalAlignments().map((line, r) => line.map((alignment, c) => mapAlign(alignment, values[r][c])));
        }
        extra.wrap = range.getWrapStrategies().map(line => line.map(strategy => strategy === SpreadsheetApp.WrapStrategy.WRAP ? 1 : 0));
        if (row[5] === null || row[5] === undefined || String(row[5]).trim() === '') {
          extra.colw = keepCols.map((keep, i) => {
            if (!keep) return null;
            checkExecutionBudget_(startedAt);
            return sheet.getColumnWidth(startCol + i);
          }).filter(value => value !== null);
        }
        extra.rowh = keepRows.map((keep, i) => {
          if (!keep) return null;
          checkExecutionBudget_(startedAt);
          return sheet.getRowHeight(startRow + i);
        }).filter(value => value !== null);
        const merges = range.getMergedRanges().map(merge => [merge.getRow() - startRow, merge.getColumn() - startCol, merge.getNumRows(), merge.getNumColumns()]);
        extra.merges = remapMerges(merges, keepRows, keepCols);
        ['styles', 'sizes', 'bg', 'ha', 'wrap', 'chips'].forEach(key => {
          if (Object.prototype.hasOwnProperty.call(extra, key)) {
            extra[key] = hiddenFilter(extra[key], keepRows, keepCols);
          }
        });
      }
      values = hiddenFilter(values, keepRows, keepCols);
      colors = hiddenFilter(colors, keepRows, keepCols);
      const result = buildRangePayload(values, colors, {
        title: row[2],
        header: row[3] === true,
        format: row[4] === true,
        cols: row[5],
        align: row[6],
        extra: extra
      });
      if (keepRows.filter(Boolean).length * keepCols.filter(Boolean).length > LARGE_RANGE_CELLS) {
        const notice = '⚠ 範囲が大きいため、公開に時間がかかり、掲示板の文字がとても小さくなります';
        warnings.push('「' + name + '」: ' + notice);
        rowWarnings.push(notice);
      }
      ranges[name] = result.payload;
      result.warnings.forEach(warning => {
        warnings.push('「' + name + '」: ' + warning);
        rowWarnings.push(warning);
      });
      rowStatus[index] = '✔ 掲示板に表示されます' + (rowWarnings.length ? ' ／ ' + rowWarnings.join(' ／ ') : '');
    } catch (e) {
      if (e && e.executionBudgetExceeded) throw e;
      const message = e && e.message ? e.message : String(e);
      warnings.push('「' + name + '」: ' + message);
      rowStatus[index] = '⚠ ' + message;
    }
  });
  return {ranges: ranges, warnings: boundedStatusArray_(warnings), rowStatus: boundedStatusArray_(rowStatus)};
}

function sheetTimeZone_() {
  try {
    const zone = SpreadsheetApp.getActiveSpreadsheet().getSpreadsheetTimeZone();
    if (typeof zone === 'string' && zone) return zone;
  } catch (e) {}
  try {
    const zone = Session.getScriptTimeZone();
    if (typeof zone === 'string' && zone) return zone;
  } catch (e) {}
  return 'Asia/Tokyo';
}

function publish_(force, trigger, recreate) {
  const startedAt = nowMillis_();
  const lock = LockService.getDocumentLock();
  if (!lock.tryLock(trigger ? 0 : 10000)) {
    const props = PropertiesService.getDocumentProperties();
    props.setProperty('pending', Utilities.getUuid());
    props.setProperty('lastPublishOutcome', 'busy');
    writeSheetStatus_();
    return false;
  }
  const props = PropertiesService.getDocumentProperties();
  if (!PropertiesService.getUserProperties().getProperty('token')) {
    props.setProperty('pending', Utilities.getUuid());
    props.setProperty('lastPublishOutcome', 'no_token');
    lock.releaseLock();
    return false;
  }
  let token;
  try { token = refreshAccessToken_(false); }
  catch (e) {
    lock.releaseLock();
    if (e.authAction === 'temporary') {
      props.setProperty('pending', Utilities.getUuid());
      props.setProperty('lastError', new Date().toISOString() + ' ' + errorText_(e));
      props.setProperty('lastPublishStatus', 'failure');
      props.setProperty('lastPublishOutcome', 'failure');
      writeSheetStatus_();
    }
    throw e;
  }
  const login = PropertiesService.getUserProperties().getProperty('login');
  let owner = props.getProperty('gistOwner');
  if (props.getProperty('gistId') && !login) {
    lock.releaseLock();
    throw new Error('GitHubのアカウントを確認できませんでした。GitHub連携をやり直してください。');
  }
  if (props.getProperty('gistId') && !owner) {
    owner = login;
    props.setProperty('gistOwner', owner);
  }
  if (!recreate && props.getProperty('gistId') && !isGistOwner_()) {
    if (trigger) { removeTriggers_(); lock.releaseLock(); return false; }
    const error = new Error(ownerMismatchMessage_(owner));
    lock.releaseLock();
    throw error;
  }
  try {
    const data = readRanges_(startedAt);
    const rowStatus = JSON.stringify(boundedStatusArray_(data.rowStatus));
    if (props.getProperty('rowStatus') !== rowStatus) setBoundedProperty_(props, 'rowStatus', rowStatus);
    const previousContentBytes = Number(props.getProperty('lastContentBytes') || 0);
    const iosWarning = '掲示板の内容が大きいため、iPhone 版 VRChat では表示できないことがあります。範囲を分けると確実です。';
    if (previousContentBytes > IOS_TRUNCATION_WARN_BYTES &&
        !data.warnings.includes(iosWarning)) {
      data.warnings.unshift(iosWarning);
    }
    const warnings = JSON.stringify(boundedStatusArray_(data.warnings));
    if (props.getProperty('warnings') !== warnings) setBoundedProperty_(props, 'warnings', warnings);
    const ranges = data.ranges;
    const rev = computeRev_(ranges);
    const previousRev = props.getProperty('lastRev');
    let id = props.getProperty('gistId');
    const pending = props.getProperty('pending');
    if (!force && !pending && rev === previousRev && id &&
        nowMillis_() - Number(props.getProperty('lastPublishMillis') || 0) < HEARTBEAT_MINUTES * 60000) {
      const current = props.getProperties();
      const updates = {};
      if (current.lastPublishStatus !== 'success') updates.lastPublishStatus = 'success';
      if (current.lastError !== '') updates.lastError = '';
      if (current.lastPublishOutcome !== 'success') updates.lastPublishOutcome = 'success';
      if (Object.keys(updates).length) props.setProperties(updates);
      setPublishedNames_(props, Object.keys(ranges));
      writeSheetStatus_();
      return true;
    }
    const now = new Date();
    const previousUnix = Number(props.getProperty('updatedUnix'));
    const updatedUnix = rev === previousRev && previousUnix > 0 ? previousUnix : Math.floor(now.getTime() / 1000);
    const updatedDate = new Date(updatedUnix * 1000);
    const offsetText = Utilities.formatDate(updatedDate, sheetTimeZone_(), 'Z');
    const offset = (offsetText[0] === '-' ? -1 : 1) * (Number(offsetText.slice(1, 3)) * 60 + Number(offsetText.slice(3, 5)));
    const content = JSON.stringify({
      format: 'sheetboard/1',
      rev: rev,
      updated: isoWithOffset(updatedDate, offset),
      updatedUnix: updatedUnix,
      aliveUnix: Math.floor(now.getTime() / 1000),
      order: Object.keys(ranges),
      ranges: ranges
    });
    const contentBytes = utf8ByteLength_(content);
    if (contentBytes > GIST_RAW_MAX_BYTES) throw new Error('掲示板に出す内容が 10MB を超えたため公開できません（GitHub の Gist が全文を返せる上限です）。範囲を小さくするか分けてください。');
    if (contentBytes > IOS_TRUNCATION_WARN_BYTES) {
      if (!data.warnings.includes(iosWarning)) data.warnings.unshift(iosWarning);
    } else {
      data.warnings = data.warnings.filter(warning => warning !== iosWarning);
    }
    const finalWarnings = JSON.stringify(boundedStatusArray_(data.warnings));
    if (props.getProperty('warnings') !== finalWarnings) setBoundedProperty_(props, 'warnings', finalWarnings);
    checkExecutionBudget_(startedAt);

    let recreated = !!(id && recreate);
    if (!id || recreate) id = createGist_(content, token);
    else {
      if (!login) throw new Error('GitHubのアカウントを確認できませんでした。GitHub連携をやり直してください。');
      if (!props.getProperty('worldUrl')) props.setProperty('worldUrl', makeWorldUrl_(owner, id));
      const files = {}; files[GIST_FILENAME] = {content: content};
      try {
        checkExecutionBudget_(startedAt);
        githubRequest_('patch', '/gists/' + encodeURIComponent(id), {files: files}, token);
      } catch (e) {
        if (e.status !== 404) throw e;
        id = createGist_(content, token);
        recreated = true;
      }
    }
    const state = {
      lastRev: rev,
      updatedUnix: String(updatedUnix),
      lastPublishMillis: String(now.getTime()),
      lastPublishTime: now.toISOString(),
      lastPublishStatus: 'success',
      lastError: ''
    };
    if (props.getProperty('lastContentBytes') !== String(contentBytes)) state.lastContentBytes = String(contentBytes);
    props.setProperties(state);
    Object.keys(props.getProperties())
      .filter(key => key.startsWith('lastMail_') && key.indexOf('urlChange') < 0)
      .forEach(key => props.deleteProperty(key));
    if (pending && props.getProperty('pending') === pending) props.deleteProperty('pending');
    if (recreated) {
      const warning = '公開先が新しくなりました。ワールドのURLを差し替えてください: ' + worldUrl_();
      props.setProperty('urlChangeNotice', warning);
      try {
        notifyMail_('urlChange', 'SheetBoardのURLが変わりました', warning, props);
      } catch (e) {
        props.setProperty('lastError', new Date().toISOString() + ' URL変更のお知らせメールを送れませんでした。');
      }
    }
    props.setProperty('lastTriggerMillis', String(nowMillis_()));
    props.setProperty('lastPublishOutcome', 'success');
    setPublishedNames_(props, Object.keys(ranges));
    writeSheetStatus_();
    return true;
  } catch (e) {
    const message = errorText_(e);
    if (e.authAction === 'temporary') props.setProperty('pending', Utilities.getUuid());
    props.setProperty('lastError', new Date(nowMillis_()).toISOString() + ' ' + message);
    props.setProperty('lastPublishStatus', 'failure');
    props.setProperty('lastPublishOutcome', e.authAction === 'reconnect' || e.status === 401 ? 'reauth_required' : 'failure');
    writeSheetStatus_();
    notifyError_(e, message, props);
    throw e;
  } finally {
    lock.releaseLock();
  }
}

function notifyError_(e, message, props) {
  const status = overallStatus_();
  const body = 'SheetBoard: 掲示板の公開が止まっています\n\n' + status.text +
    '\n\nあなたがすること:\n1. ' + (status.action || '数分待ってから掲示板を確認してください。') +
    '\n2. 直らない場合は、スプレッドシートのメニュー「SheetBoard → 状態とURLを表示」を確認してください。' +
    '\n\nスプレッドシート: ' + SpreadsheetApp.getActiveSpreadsheet().getUrl();
  try {
    notifyMail_(e && e.status ? String(e.status) : 'other', 'SheetBoard: 掲示板の公開が止まっています', body, props);
  } catch (ignored) {}
}
function notifyMail_(kind, subject, body, props) {
  const key = 'lastMail_' + kind;
  if (Date.now() - Number(props.getProperty(key) || 0) < 24 * 60 * 60 * 1000) return;
  sendMail_(subject, body);
  props.setProperty(key, String(Date.now()));
}

function sendMail_(subject, body) {
  const email = Session.getEffectiveUser().getEmail();
  if (!email) throw new Error('メールアドレスを確認できませんでした。');
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  MailApp.sendEmail(email, subject, body + '\n\nスプレッドシート: ' + ss.getName() + '\n' + ss.getUrl());
}

function showStatus() {
  const props = PropertiesService.getDocumentProperties();
  const user = PropertiesService.getUserProperties();
  let warnings = [];
  try {
    warnings = JSON.parse(props.getProperty('warnings') || '[]');
  } catch (e) {}
  const status = overallStatus_();
  const url = worldUrl_();
  const owner = props.getProperty('gistOwner') || '未設定';
  const login = user.getProperty('login');
  const who = login ? login + (login === owner ? '（管理者）' : '') : 'GitHub未連携（スタッフの方は操作不要です）';
  const formatTime = value => value ? Utilities.formatDate(new Date(value), sheetTimeZone_(), 'yyyy/MM/dd HH:mm') : 'まだ公開していません';
  const updated = Number(props.getProperty('updatedUnix') || 0);
  let names = [];
  try {
    names = JSON.parse(props.getProperty('publishedNames') || '[]');
  } catch (e) {}
  const nameSummary = names.length
    ? names.join('、') + (props.getProperty('publishedNamesIncomplete') === 'true' ? '、…（ほか ' + Math.max(0, publishedNameCount_(props) - names.length) + ' 件）' : '')
    : '範囲名は保存上限を超えています（' + publishedNameCount_(props) + ' 件）';
  let error = props.getProperty('lastError') || '';
  if (error) {
    const match = error.match(/^(\d{4}-\d\d-\d\dT[^ ]+)\s*(.*)$/);
    if (match) error = formatTime(match[1]) + ' ' + match[2];
  }
  const notice = props.getProperty('urlChangeNotice');
  const allWarnings = (notice ? [notice] : []).concat(warnings);
  const state = '<div class="status ' + status.level + '"><b>' + escapeHtml_(status.icon + ' ' + status.text) + '</b>' +
    (status.action ? '<p>' + escapeHtml_(status.action) + '</p>' : '') + '</div>';
  const urlHtml = url
    ? '<input class="url" id="worldUrl" readonly value="' + escapeHtml_(url) + '"><button id="copyworld" onclick="copyText(\'worldUrl\')">コピー</button>'
    : 'まだありません';
  const details = '<table><tr><td>公開を担当しているGitHub</td><td>' + escapeHtml_(owner) + '</td></tr>' +
    '<tr><td>あなた</td><td>' + escapeHtml_(who) + '</td></tr>' +
    '<tr><td>最終公開</td><td>' + escapeHtml_(formatTime(props.getProperty('lastPublishTime'))) + '</td></tr>' +
    '<tr><td>掲示板の内容の更新</td><td>' + escapeHtml_(updated ? Utilities.formatDate(new Date(updated * 1000), sheetTimeZone_(), 'yyyy/MM/dd HH:mm') : 'まだ公開していません') + '</td></tr>' +
    '<tr><td>公開中の範囲</td><td>' + escapeHtml_(publishedNameCount_(props) ? nameSummary : 'なし') + '</td></tr></table>';
  const warningsHtml = allWarnings.length
    ? allWarnings.map(item => '<li>' + escapeHtml_(item) + '</li>').join('')
    : '<li>ありません</li>';
  showHtml_('状態とURL', state + '<h3>ワールドに貼るURL</h3>' + urlHtml + details +
    (error ? '<h3>直近のエラー</h3><p>' + escapeHtml_(error) + '</p>' : '') +
    '<h3>お知らせ</h3><ul>' + warningsHtml + '</ul><script></script>');
}
function disconnectSheetBoard() {
  const user = PropertiesService.getUserProperties();
  if (!user.getProperty('token')) {
    showHtml_('連携を解除', '<p>あなたはGitHubと連携していないため、解除するものはありません。</p>');
    return;
  }
  showHtml_('連携を解除', '<p>解除すると自動公開が止まり、掲示板は更新されなくなります（URLと過去の公開内容は残ります）。</p>' +
    '<button onclick="disconnect(this)">連携を解除する</button><p id="status"></p>' +
    '<script>function disconnect(b){busy(b);google.script.run.withSuccessHandler(function(){document.getElementById("status").textContent="解除しました。この画面は閉じてかまいません"}).withFailureHandler(function(e){b.disabled=false;fail(e)}).disconnectConfirmed()}' +
    '</script>');
}
function recreateDestination() {
  const props = PropertiesService.getDocumentProperties();
  const user = PropertiesService.getUserProperties();
  const owner = escapeHtml_(props.getProperty('gistOwner') || '未設定');
  const login = user.getProperty('login') || '';
  const identity = login ? escapeHtml_(login) : 'GitHub未連携';
  const admin = login && login === props.getProperty('gistOwner')
    ? '<p class="status warn">あなたは現在の管理者です。通常は作り直す必要はありません。</p>'
    : '';
  const button = user.getProperty('token')
    ? '<button id="run" disabled onclick="recreate(this)">公開先を作り直す</button>'
    : '<button id="run" disabled onclick="beginHandoff(this)">GitHubと連携して続ける</button>';
  const html = '<p>公開を担当しているGitHub: ' + owner + '<br>あなた: ' + identity + '</p>' +
    '<p>作り直すと<b>新しいURL</b>になり、今ワールドに貼ってあるURLの掲示板は<b>更新されなくなります</b>。ワールド作者にURLを差し替えて再アップロードしてもらう必要があります。</p>' +
    '<p>使う場面: 管理者が交代し、前の管理者のGitHubを使えなくなったときだけ。</p>' + admin +
    '<label><input type="checkbox" id="confirm"> 上の内容を理解しました</label>' + button +
    '<p id="status"></p><div id="newurlresult" style="display:none"></div>' +
    '<script>document.getElementById("confirm").onchange=function(){document.getElementById("run").disabled=!this.checked};' +
    'function recreate(b){busy(b);google.script.run.withSuccessHandler(function(r){var x=document.getElementById("newurlresult");x.style.display="block";' +
    'x.innerHTML="<input id=newurl class=url readonly><button id=copynew>コピー</button>";' +
    'document.getElementById("copynew").onclick=function(){copyText("newurl")};document.getElementById("newurl").value=r.url;' +
    'document.getElementById("status").textContent="ワールド作者に新しいURLを渡してください。";' +
    '}).withFailureHandler(function(e){b.disabled=false;fail(e)}).recreateDestinationConfirmed()}' +
    '</script>';
  showHtml_('公開先を作り直す', html);
}
function beginHandoffAuthorization() {
  beginAuthorization_('handoff');
}
function recreateDestinationConfirmed() {
  if (!PropertiesService.getUserProperties().getProperty('token')) {
    throw new Error('先にGitHubを連携してください。');
  }
  if (!publish_(true, false, true)) throw new Error(publishFailureMessage_());
  ensureTriggers_();
  return {url: worldUrl_()};
}
function disconnectConfirmed() {
  const user = PropertiesService.getUserProperties();
  const lock = LockService.getUserLock();
  if (!lock.tryLock(10000)) {
     throw makeError_('連携を解除できませんでした。公開処理が終わってから、もう一度お試しください。', 503);
  }
  try {
    const epoch = Number(user.getProperty('authEpoch') || 0) + 1;
    user.setProperty('authEpoch', String(epoch));
    ['token', 'login', 'deviceCode', 'authType', 'refreshToken', 'tokenExpiresAt', 'refreshTokenExpiresAt', 'reauthRequired']
      .forEach(key => user.deleteProperty(key));
  } finally {
    lock.releaseLock();
  }
  removeTriggers_();
  writeSheetStatus_();
}
function escapeHtml_(value) {
  return String(value === undefined || value === null ? '' : value).replace(/[&<>"']/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'}[c]));
}
function showHtml_(title, html, options) {
  SpreadsheetApp.getUi().showModalDialog(HtmlService.createHtmlOutput(page_(html, options)).setWidth(520).setHeight(480), title);
}
function showMessage_(title, message) { showHtml_(title, '<p style="white-space:pre-wrap">' + escapeHtml_(message) + '</p>'); }
