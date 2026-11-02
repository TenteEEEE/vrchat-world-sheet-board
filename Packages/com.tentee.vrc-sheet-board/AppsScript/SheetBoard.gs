/**
 * SheetBoard: シートの表をワールドに公開するためのスクリプトです。
 * シートを開き直し、メニューの「はじめにセットアップ」から始めてください。
 */
const GITHUB_CLIENT_ID = 'Ov23liVU3bFAK2lWtKPk'; // Sheet Board OAuth App（Device Flow）の公開クライアントIDです。秘密情報ではありません。フォークでは独自のIDに置き換えられます。空にするとトークン貼り付け方式になります。
const GIST_FILENAME = 'sheetboard.json';
const SETTINGS_SHEET = 'SheetBoard設定';
const MAX_RANGES = 20;
const MAX_ROWS = 100;
const MAX_COLS = 20;
const MAX_CELL_CHARS = 200;
const MAX_JSON_BYTES = 500 * 1024;
const HEARTBEAT_MINUTES = 60;

function trimEmpty(values, layers, colors) {
  const legacy = Array.isArray(layers) || arguments.length >= 3;
  layers = legacy ? {weights: layers, colors: colors} : (layers || {});
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
  return legacy ? {values: crop(values, ''), weights: result.weights, colors: result.colors} : {values: crop(values, ''), layers: result};
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

function remapMerges(merges, keepRows, keepCols, rowLimit, colLimit) {
  const rows = keepRows.map((keep, i) => keep ? keepRows.slice(0, i).filter(Boolean).length : -1);
  const cols = keepCols.map((keep, i) => keep ? keepCols.slice(0, i).filter(Boolean).length : -1);
  const result = [];
  (merges || []).forEach(merge => {
    const rs = rows.slice(Math.max(0, merge[0]), Math.max(0, merge[0] + merge[2])).filter(i => i >= 0 && i < rowLimit);
    const cs = cols.slice(Math.max(0, merge[1]), Math.max(0, merge[1] + merge[3])).filter(i => i >= 0 && i < colLimit);
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

function normalizeColor(hex) {
  let value = String(hex || '').trim();
  if (/^#[0-9a-f]{3}$/i.test(value)) value = '#' + value.slice(1).split('').map(c => c + c).join('');
  if (!/^#[0-9a-f]{6}$/i.test(value)) return '';
  value = value.toUpperCase();
  return value === '#000000' ? '' : value;
}

function normalizeBackground_(hex) {
  let value = String(hex || '').trim();
  if (/^#[0-9a-f]{3}$/i.test(value)) value = '#' + value.slice(1).split('').map(c => c + c).join('');
  if (!/^#[0-9a-f]{6}$/i.test(value)) return '';
  value = value.toUpperCase();
  return value === '#FFFFFF' ? '' : value;
}

function buildRangePayload(values, weights, colors, options) {
  options = options || {};
  const extra = options.extra || {};
  const layers = {weights: weights, colors: colors};
  ['styles', 'sizes', 'bg', 'ha', 'wrap'].forEach(key => {
    if (Object.prototype.hasOwnProperty.call(extra, key)) layers[key] = extra[key];
  });
  const trimmed = trimEmpty(values, layers);
  const warnings = [];
  if (trimmed.values.length > MAX_ROWS) warnings.push('行数が100行を超えたため、残りを省きました。');
  if (trimmed.values.some(row => row.length > MAX_COLS)) warnings.push('列数が20列を超えたため、残りを省きました。');
  let truncated = false;
  const rows = trimmed.values.slice(0, MAX_ROWS).map(row => row.slice(0, MAX_COLS).map(value => {
    const s = value === null || value === undefined ? '' : String(value);
    if (s.length > MAX_CELL_CHARS) truncated = true;
    return s.slice(0, MAX_CELL_CHARS);
  }));
  if (truncated) warnings.push('200文字を超えるセルを短くしました。');
  const payload = {header: !!options.header, rows: rows};
  if (options.title !== undefined && options.title !== null && String(options.title).trim()) payload.title = String(options.title);
  const cols = Array.isArray(options.cols) ? options.cols : parseRatioList(options.cols);
  const align = Array.isArray(options.align) ? options.align : parseAlignList(options.align);
  if (cols.length) payload.cols = cols.slice(0, MAX_COLS);
  if (align.length) payload.align = align.slice(0, MAX_COLS);
  if (options.format) {
    payload.color = rows.map((row, r) => row.map((_, c) => normalizeColor(trimmed.layers.colors[r][c])));
  }
  [['styles', 'style'], ['sizes', 'size'], ['bg', 'bg'], ['ha', 'ha'], ['wrap', 'wrap']].forEach(pair => {
    if (Object.prototype.hasOwnProperty.call(extra, pair[0])) {
      payload[pair[1]] = rows.map((row, r) => row.map((_, c) => trimmed.layers[pair[0]][r][c]));
    }
  });
  const width = rows.length ? rows[0].length : 0;
  if (Object.prototype.hasOwnProperty.call(extra, 'colw')) payload.colw = extra.colw.slice(0, width);
  if (Object.prototype.hasOwnProperty.call(extra, 'rowh')) payload.rowh = extra.rowh.slice(0, rows.length);
  if (Object.prototype.hasOwnProperty.call(extra, 'merges')) {
    payload.merges = remapMerges(extra.merges, Array(trimmed.values.length).fill(true), Array(trimmed.values[0] ? trimmed.values[0].length : 0).fill(true), rows.length, width);
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
  SpreadsheetApp.getUi().createMenu('SheetBoard')
    .addItem('はじめにセットアップ', 'startSetup')
    .addItem('今すぐ公開', 'publishNow')
    .addItem('状態とURLを表示', 'showStatus')
    .addItem('GitHub連携をやり直す', 'reconnectGitHub')
    .addItem('連携を解除', 'disconnectSheetBoard')
    .addItem('公開先を作り直す', 'recreateDestination').addToUi();
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
}

function startSetup() {
  ensureSettingsSheet_();
  if (PropertiesService.getUserProperties().getProperty('token')) showFinishDialog_();
  else beginAuthorization_(true);
}

function reconnectGitHub() { beginAuthorization_(false); }

function beginAuthorization_(setup) {
  if (!GITHUB_CLIENT_ID) {
    showHtml_('GitHub連携', '<p>公開内容はURLを知っている人なら誰でも読めます。</p><p>GitHubで作成した、gist権限のあるPersonal access token（classic）を貼り付けてください。</p>' +
      '<input id="token" type="password" style="width:95%"><p><button onclick="save()">連携する</button></p>' +
      '<p id="status"></p><script>function save(){document.getElementById("status").textContent="確認しています…";' +
      'google.script.run.withSuccessHandler(function(){' + (setup ? 'finish()' : 'document.getElementById("status").textContent="連携しました。"') +
      '}).withFailureHandler(fail).saveToken(document.getElementById("token").value, true)}' + clientHelpers_() + '</script>');
    return;
  }
  try {
    const response = githubForm_('https://github.com/login/device/code', {client_id: GITHUB_CLIENT_ID, scope: 'gist'});
    if (!response.device_code || !response.user_code || !/^https:\/\/github\.com\/login\/device\/?$/.test(response.verification_uri || '')) throw new Error('連携を開始できませんでした。');
    const user = PropertiesService.getUserProperties();
    user.setProperty('deviceCode', response.device_code);
    const interval = Math.max(1, Number(response.interval) || 5);
    const code = escapeHtml_(response.user_code);
    const uri = escapeHtml_(response.verification_uri);
    showHtml_('GitHub連携', '<p>公開内容はURLを知っている人なら誰でも読めます。</p><p>GitHubのページを開いて、このコードを入力してください。</p><div id="code" style="font-size:38px;font-weight:bold">' + code +
      '</div><p><a href="' + uri + '" target="_blank" rel="noopener">GitHubを開く</a> <button onclick="copyCode()">コードをコピー</button></p>' +
      '<p id="status">承認を待っています…</p><script>let retries=0,delay=' + interval * 1000 + ';' +
      'function copyCode(){navigator.clipboard.writeText(document.getElementById("code").textContent)}' +
      'function poll(){google.script.run.withSuccessHandler(function(r){retries=0;if(r.status==="success"){' +
      (setup ? 'finish()' : 'document.getElementById("status").textContent="連携しました。"') +
      '}else if(r.status==="pending"){if(r.slow)delay+=5000;setTimeout(poll,delay)}else{document.getElementById("status").textContent=r.message}})' +
      '.withFailureHandler(function(e){if(retries++<3)setTimeout(poll,delay);else fail(e)}).pollDeviceToken()}setTimeout(poll,delay);' + clientHelpers_() + '</script>');
  } catch (e) { showMessage_('GitHub連携', errorText_(e)); }
}

function clientHelpers_() {
  return 'function fail(e){document.getElementById("status").textContent=e.message||"処理できませんでした。"}' +
    'function finish(){document.getElementById("status").textContent="公開の準備をしています…";' +
    'google.script.run.withSuccessHandler(function(r){document.getElementById("status").textContent="公開しました。ワールドに貼るURL: "+r.url})' +
    '.withFailureHandler(fail).finishSetup()}';
}

function showFinishDialog_() {
  showHtml_('セットアップ', '<p>設定を確認して公開します。公開内容はURLを知っている人なら誰でも読めます。</p>' +
    '<button onclick="finish()">セットアップを続ける</button><p id="status"></p><script>' + clientHelpers_() + '</script>');
}

function githubForm_(url, data) {
  const response = UrlFetchApp.fetch(url, {method: 'post', payload: data, headers: {Accept: 'application/json'}, muteHttpExceptions: true});
  const status = response.getResponseCode();
  if (status < 200 || status >= 300) throw makeError_('GitHubへの接続に失敗しました（' + status + '）。', status);
  return JSON.parse(response.getContentText());
}

function pollDeviceToken() {
  const user = PropertiesService.getUserProperties();
  const code = user.getProperty('deviceCode');
  if (!code) return {status: 'error', message: '連携をもう一度始めてください。'};
  const result = githubForm_('https://github.com/login/oauth/access_token', {client_id: GITHUB_CLIENT_ID, device_code: code, grant_type: 'urn:ietf:params:oauth:grant-type:device_code'});
  if (result.access_token) {
    saveToken_(result.access_token, false);
    user.deleteProperty('deviceCode');
    return {status: 'success'};
  }
  if (result.error === 'authorization_pending' || result.error === 'slow_down') return {status: 'pending', slow: result.error === 'slow_down'};
  user.deleteProperty('deviceCode');
  const message = result.error === 'expired_token' ? 'コードの有効期限（15分）が切れました。' :
    result.error === 'access_denied' ? 'GitHubで承認がキャンセルされました。' : '連携できませんでした（' + String(result.error || 'unknown') + '）。';
  return {status: 'error', message: message + 'メニューから連携をもう一度始めてください。'};
}

function saveToken(token, checkScopes) { saveToken_(token, checkScopes); }
function saveToken_(token, checkScopes) {
  token = String(token || '').trim();
  if (!token) throw new Error('トークンを入力してください。');
  let profile;
  try { profile = githubRequest_('get', '/user', null, token); }
  catch (e) { throw makeError_('GitHubのアカウントを確認できませんでした（' + (e.status || '接続エラー') + '）。', e.status); }
  if (!profile.body.login) throw new Error('GitHubのアカウントを確認できませんでした（' + profile.status + '）。');
  if (checkScopes && !String(profile.scopes || '').split(',').some(s => s.trim() === 'gist')) throw new Error('gist権限のあるPersonal access token（classic）を入力してください。');
  PropertiesService.getUserProperties().setProperties({token: token, login: profile.body.login});
  ensureTriggers_();
}

function githubRequest_(method, path, body, token) {
  token = token || PropertiesService.getUserProperties().getProperty('token');
  if (!token) throw makeError_('GitHub連携が必要です。メニューから連携してください。', 401);
  const options = {method: method, headers: {Authorization: 'Bearer ' + token, Accept: 'application/vnd.github+json', 'X-GitHub-Api-Version': '2022-11-28'}, muteHttpExceptions: true};
  if (body !== null && body !== undefined) { options.contentType = 'application/json'; options.payload = JSON.stringify(body); }
  const response = UrlFetchApp.fetch('https://api.github.com' + path, options);
  const status = response.getResponseCode();
  if (status < 200 || status >= 300) throw makeError_('GitHubへの公開に失敗しました（' + status + '）。', status);
  const headers = response.getAllHeaders();
  const scopeKey = Object.keys(headers).find(k => k.toLowerCase() === 'x-oauth-scopes');
  return {status: status, body: JSON.parse(response.getContentText() || '{}'), scopes: scopeKey ? headers[scopeKey] : ''};
}

function makeError_(message, status) { const e = new Error(message); e.status = status; return e; }
function errorText_(e) { return e && e.message ? e.message : '処理できませんでした。'; }

function createGist_(content) {
  const user = PropertiesService.getUserProperties();
  let login = user.getProperty('login');
  if (!login) {
    let profile;
    try { profile = githubRequest_('get', '/user', null); }
    catch (e) { throw makeError_('GitHubのアカウントを確認できませんでした（' + (e.status || '接続エラー') + '）。', e.status); }
    login = profile.body.login;
    if (!login) throw new Error('GitHubのアカウントを確認できませんでした（' + profile.status + '）。');
    user.setProperty('login', login);
  }
  const files = {}; files[GIST_FILENAME] = {content: content};
  const gist = githubRequest_('post', '/gists', {description: 'Sheet Board: ' + SpreadsheetApp.getActiveSpreadsheet().getName(), public: false, files: files}).body;
  if (!gist.id) throw new Error('公開先を作成できませんでした。');
  PropertiesService.getDocumentProperties().setProperties({gistId: gist.id, gistOwner: login, worldUrl: makeWorldUrl_(login, gist.id)});
  return gist.id;
}

function worldUrl_() {
  return PropertiesService.getDocumentProperties().getProperty('worldUrl') || '';
}
function makeWorldUrl_(login, id) { return 'https://gist.githubusercontent.com/' + encodeURIComponent(login) + '/' + encodeURIComponent(id) + '/raw/' + GIST_FILENAME; }

function checkGistOwner_() {
  const props = PropertiesService.getDocumentProperties();
  const owner = props.getProperty('gistId') && props.getProperty('gistOwner');
  if (!owner) return;
  const login = PropertiesService.getUserProperties().getProperty('login');
  if (!login) throw new Error('GitHubのアカウントを確認できませんでした。GitHub連携をやり直してください。');
  if (owner.toLowerCase() !== login.toLowerCase()) throw new Error('この公開先は ' + owner + ' さんのGitHubにあります。管理者を交代する場合は、メニューの『公開先を作り直す』を使ってください。');
}

function finishSetup() {
  ensureSettingsSheet_();
  if (!PropertiesService.getUserProperties().getProperty('token')) throw new Error('先にGitHubを連携してください。');
  checkGistOwner_();
  installTriggers_();
  const result = publish_(true);
  if (!result) throw new Error('別の公開処理が進行中です。少し待ってからもう一度お試しください。');
  PropertiesService.getDocumentProperties().deleteProperty('urlChangeNotice');
  return {url: worldUrl_()};
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
function onEditInstalled() {
  const last = Number(PropertiesService.getDocumentProperties().getProperty('lastPublishMillis') || 0);
  if (Date.now() - last < 5000) { setPending_(); return; }
  try { publish_(false, true); } catch (e) {}
}
function onChangeInstalled(e) {
  if (!e || !['FORMAT', 'INSERT_ROW', 'REMOVE_ROW', 'INSERT_COLUMN', 'REMOVE_COLUMN', 'OTHER'].includes(e.changeType)) return;
  const last = Number(PropertiesService.getDocumentProperties().getProperty('lastPublishMillis') || 0);
  if (Date.now() - last < 5000) { setPending_(); return; }
  try { publish_(false, true); } catch (e) {}
}
function onTimer() { try { publish_(false, true); } catch (e) {} }
function setPending_() { PropertiesService.getDocumentProperties().setProperty('pending', Utilities.getUuid()); }
function publishNow() {
  try {
    if (!PropertiesService.getUserProperties().getProperty('token')) {
      setPending_();
      showMessage_('公開', '次の自動確認（最大5分）で公開されます。');
      return;
    }
    if (publish_(true)) showMessage_('公開', '公開しました。\n' + worldUrl_());
    else showMessage_('公開', '別の公開処理が進行中です。少し待ってからお試しください。');
  } catch (e) { showMessage_('公開できませんでした', errorText_(e)); }
}

function readRanges_() {
  const ss = SpreadsheetApp.getActiveSpreadsheet();
  const settings = ss.getSheetByName(SETTINGS_SHEET);
  if (!settings) throw new Error('設定タブがありません。はじめにセットアップを実行してください。');
  const config = settings.getLastRow() > 1 ? settings.getRange(2, 1, settings.getLastRow() - 1, 7).getValues() : [];
  const ranges = Object.create(null);
  const seen = Object.create(null);
  const warnings = [];
  config.forEach(row => {
    const name = String(row[0] === null ? '' : row[0]).trim();
    if (!name || name.startsWith('#')) return;
    if (Object.prototype.hasOwnProperty.call(seen, name)) { warnings.push('名前「' + name + '」が重複しています。先の行を使います。'); return; }
    seen[name] = true;
    if (Object.keys(ranges).length >= MAX_RANGES) { warnings.push('範囲は20個までです。「' + name + '」を省きました。'); return; }
    try {
      const ref = parseRangeRef(row[1]);
      if (!ref || !ref.a1) throw new Error('範囲を入力してください。');
      const sheet = ref.sheet ? ss.getSheetByName(ref.sheet) : ss.getSheets().find(s => s.getName() !== SETTINGS_SHEET);
      if (!sheet) throw new Error('シート「' + ref.sheet + '」が見つかりません。');
      let range;
      try { range = sheet.getRange(ref.a1); }
      catch (e) { throw new Error('範囲「' + ref.a1 + '」を読めません。'); }
      const startRow = range.getRow();
      const startCol = range.getColumn();
      const rows = Math.min(range.getNumRows(), MAX_ROWS, Math.max(0, sheet.getLastRow() - startRow + 1));
      const cols = Math.min(range.getNumColumns(), MAX_COLS, Math.max(0, sheet.getLastColumn() - startCol + 1));
      if (Math.min(range.getNumRows(), Math.max(0, sheet.getLastRow() - startRow + 1)) > MAX_ROWS) warnings.push('「' + name + '」: 行数が100行を超えたため、残りを省きました。');
      if (Math.min(range.getNumColumns(), Math.max(0, sheet.getLastColumn() - startCol + 1)) > MAX_COLS) warnings.push('「' + name + '」: 列数が20列を超えたため、残りを省きました。');
      if (!rows || !cols) { ranges[name] = buildRangePayload([], [], [], {title: row[2], header: row[3] === true, format: row[4] === true, cols: row[5], align: row[6]}).payload; return; }
      range = sheet.getRange(startRow, startCol, rows, cols);
      const keepRows = Array.from({length: range.getNumRows()}, (_, i) =>
        !sheet.isRowHiddenByUser(startRow + i) && !sheet.isRowHiddenByFilter(startRow + i));
      const keepCols = Array.from({length: range.getNumColumns()}, (_, i) => !sheet.isColumnHiddenByUser(startCol + i));
      const validations = range.getDataValidations();
      let values = range.getDisplayValues().map((line, r) => line.map((value, c) => {
        const validation = validations[r][c];
        if (validation && validation.getCriteriaType() === SpreadsheetApp.DataValidationCriteria.CHECKBOX) {
          if (String(value).toUpperCase() === 'TRUE') return '■';
          if (String(value).toUpperCase() === 'FALSE') return '□';
        }
        return value;
      }));
      let weights = row[4] === true ? range.getFontWeights() : [];
      let colors = [];
      const extra = {};
      if (row[4] === true) {
        colors = range.getFontColorObjects().map(line => line.map(color => {
          try { return color.asRgbColor().asHexString(); } catch (e) { return ''; }
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
          extra.colw = keepCols.map((keep, i) => keep ? sheet.getColumnWidth(startCol + i) : null).filter(value => value !== null);
        }
        extra.rowh = keepRows.map((keep, i) => keep ? sheet.getRowHeight(startRow + i) : null).filter(value => value !== null);
        const merges = range.getMergedRanges().map(merge => [merge.getRow() - startRow, merge.getColumn() - startCol, merge.getNumRows(), merge.getNumColumns()]);
        extra.merges = remapMerges(merges, keepRows, keepCols, MAX_ROWS, MAX_COLS);
        ['styles', 'sizes', 'bg', 'ha', 'wrap'].forEach(key => {
          if (Object.prototype.hasOwnProperty.call(extra, key)) extra[key] = hiddenFilter(extra[key], keepRows, keepCols);
        });
      }
      values = hiddenFilter(values, keepRows, keepCols);
      weights = hiddenFilter(weights, keepRows, keepCols);
      colors = hiddenFilter(colors, keepRows, keepCols);
      const result = buildRangePayload(values, weights, colors, {title: row[2], header: row[3] === true, format: row[4] === true, cols: row[5], align: row[6], extra: extra});
      ranges[name] = result.payload;
      result.warnings.forEach(w => warnings.push('「' + name + '」: ' + w));
    } catch (e) { warnings.push('「' + name + '」: ' + errorText_(e)); }
  });
  return {ranges: ranges, warnings: warnings};
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
  const lock = LockService.getDocumentLock();
  if (!lock.tryLock(trigger ? 0 : 10000)) { setPending_(); return false; }
  const props = PropertiesService.getDocumentProperties();
  if (!PropertiesService.getUserProperties().getProperty('token')) { setPending_(); lock.releaseLock(); return false; }
  try {
    const pending = props.getProperty('pending');
    const data = readRanges_();
    const warnings = JSON.stringify(data.warnings);
    if (props.getProperty('warnings') !== warnings) props.setProperty('warnings', warnings);
    const rev = computeRev_(data.ranges);
    const previousRev = props.getProperty('lastRev');
    if (!force && !pending && rev === previousRev && props.getProperty('gistId') &&
        Date.now() - Number(props.getProperty('lastPublishMillis') || 0) < HEARTBEAT_MINUTES * 60000) return true;
    const now = new Date();
    const previousUnix = Number(props.getProperty('updatedUnix'));
    const updatedUnix = rev === previousRev && previousUnix > 0 ? previousUnix : Math.floor(now.getTime() / 1000);
    const updatedDate = new Date(updatedUnix * 1000);
    const zone = sheetTimeZone_();
    const offsetText = Utilities.formatDate(updatedDate, zone, 'Z');
    const offset = (offsetText[0] === '-' ? -1 : 1) * (Number(offsetText.slice(1, 3)) * 60 + Number(offsetText.slice(3, 5)));
    const content = JSON.stringify({format: 'sheetboard/1', rev: rev, updated: isoWithOffset(updatedDate, offset), updatedUnix: updatedUnix, aliveUnix: Math.floor(now.getTime() / 1000), ranges: data.ranges});
    if (Utilities.newBlob(content).getBytes().length > MAX_JSON_BYTES) throw new Error('公開するデータが500 KBを超えています。範囲を小さくしてください。');
    if (recreate) {
      props.deleteProperty('gistId');
      props.deleteProperty('gistOwner');
      props.deleteProperty('worldUrl');
    }
    let id = props.getProperty('gistId');
    let recreated = false;
    if (!id) id = createGist_(content);
    else {
      const login = PropertiesService.getUserProperties().getProperty('login');
      if (!login) throw new Error('GitHubのアカウントを確認できませんでした。GitHub連携をやり直してください。');
      let owner = props.getProperty('gistOwner');
      if (!owner) {
        owner = login;
        props.setProperties({gistOwner: owner, worldUrl: makeWorldUrl_(owner, id)});
      } else if (!props.getProperty('worldUrl')) props.setProperty('worldUrl', makeWorldUrl_(owner, id));
      checkGistOwner_();
      const files = {}; files[GIST_FILENAME] = {content: content};
      try { githubRequest_('patch', '/gists/' + encodeURIComponent(id), {files: files}); }
      catch (e) {
        if (e.status !== 404) throw e;
        id = createGist_(content);
        recreated = true;
      }
    }
    props.setProperties({lastRev: rev, updatedUnix: String(updatedUnix), lastPublishMillis: String(now.getTime()), lastPublishTime: now.toISOString(), lastError: ''});
    Object.keys(props.getProperties()).filter(key => key.startsWith('lastMail_') && key !== 'lastMail_urlChange').forEach(key => props.deleteProperty(key));
    if (props.getProperty('pending') === pending) props.deleteProperty('pending');
    if (recreated) {
      const warning = '公開先が新しくなりました。ワールドのURLを差し替えてください: ' + worldUrl_();
      props.setProperty('urlChangeNotice', warning);
      try { notifyMail_('urlChange', 'SheetBoardのURLが変わりました', warning, props); }
      catch (e) { props.setProperty('lastError', new Date().toISOString() + ' URL変更のお知らせメールを送れませんでした。'); }
    }
    return true;
  } catch (e) {
    const message = errorText_(e);
    props.setProperty('lastError', new Date().toISOString() + ' ' + message);
    notifyError_(e, message, props);
    throw e;
  } finally { lock.releaseLock(); }
}

function notifyError_(e, message, props) {
  const kind = e.status ? String(e.status) : 'other';
  let body = 'SheetBoardを公開できませんでした。\n\n' + message;
  if (e.status === 401 || e.status === 403) body += '\n\nシートのメニュー → GitHub連携をやり直す をお試しください。';
  try { notifyMail_(kind, 'SheetBoardの公開について', body, props); } catch (ignored) {}
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
  let warnings = [];
  try { warnings = JSON.parse(props.getProperty('warnings') || '[]'); } catch (e) {}
  if (props.getProperty('urlChangeNotice')) warnings.unshift(props.getProperty('urlChangeNotice'));
  const last = props.getProperty('lastPublishTime');
  const time = last ? Utilities.formatDate(new Date(last), sheetTimeZone_(), 'yyyy/MM/dd HH:mm:ss') : 'まだ公開していません';
  showHtml_('状態とURL', '<p>ワールドに貼るURL</p><input id="url" readonly style="width:95%" value="' + escapeHtml_(worldUrl_()) + '">' +
    '<p><button onclick="navigator.clipboard.writeText(document.getElementById(\'url\').value)">URLをコピー</button></p>' +
    '<p>最終公開: ' + escapeHtml_(time) + '</p><p>直近のエラー: ' + escapeHtml_(props.getProperty('lastError') || 'ありません') + '</p>' +
    '<p>お知らせ: ' + escapeHtml_(warnings.length ? warnings.join('\n') : 'ありません').replace(/\n/g, '<br>') + '</p>');
}

function disconnectSheetBoard() {
  showHtml_('連携を解除', '<p>連携を解除すると自動公開が止まります。作成したGistは削除されません。</p>' +
    '<button onclick="google.script.run.withSuccessHandler(function(){document.getElementById(\'status\').textContent=\'解除しました。\'}).withFailureHandler(function(e){document.getElementById(\'status\').textContent=e.message}).disconnectConfirmed()">連携を解除する</button><p id="status"></p>');
}
function recreateDestination() {
  if (!PropertiesService.getUserProperties().getProperty('token')) {
    beginAuthorization_(false);
    return;
  }
  showHtml_('公開先を作り直す', '<p>新しい公開先を作ります。元のGistは削除されません。ワールドのURLを差し替える必要があります。</p>' +
    '<button onclick="recreate()">公開先を作り直す</button><p id="status"></p><script>' +
    'function recreate(){document.querySelector("button").disabled=true;document.getElementById("status").textContent="公開しています…";' +
    'google.script.run.withSuccessHandler(function(r){document.getElementById("status").textContent="公開しました。ワールドのURLを差し替えてください: " + r.url})' +
    '.withFailureHandler(function(e){document.getElementById("status").textContent=e.message||"処理できませんでした。"}).recreateDestinationConfirmed()}</script>');
}
function recreateDestinationConfirmed() {
  if (!PropertiesService.getUserProperties().getProperty('token')) throw new Error('先にGitHubを連携してください。');
  if (!publish_(true, false, true)) throw new Error('別の公開処理が進行中です。少し待ってからもう一度お試しください。');
  ensureTriggers_();
  return {url: worldUrl_()};
}
function disconnectConfirmed() {
  removeTriggers_();
  const user = PropertiesService.getUserProperties();
  user.deleteProperty('token'); user.deleteProperty('login'); user.deleteProperty('deviceCode');
}
function escapeHtml_(value) {
  return String(value === undefined || value === null ? '' : value).replace(/[&<>"']/g, c => ({'&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'}[c]));
}
function showHtml_(title, html) { SpreadsheetApp.getUi().showModalDialog(HtmlService.createHtmlOutput(html).setWidth(520).setHeight(360), title); }
function showMessage_(title, message) { showHtml_(title, '<p style="white-space:pre-wrap">' + escapeHtml_(message) + '</p>'); }
