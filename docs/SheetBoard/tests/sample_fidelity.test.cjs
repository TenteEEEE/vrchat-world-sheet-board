// Run from the repository root: node --test docs/SheetBoard/tests/sample_fidelity.test.cjs
// Protects the preview fixture's rendering coverage, not renderer behavior.
const {test} = require('node:test');
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');

const fixturePath = path.resolve(__dirname, '../../../Assets/SheetBoardDev/Samples/sample_fidelity.json');
const fixture = JSON.parse(fs.readFileSync(fixturePath, 'utf8'));
const ranges = fixture.ranges;
const text = value => String(value ?? '');
const flattened = rows => rows.flat().map(text);

test('sample uses the preview-compatible format and bounded parallel layers', () => {
  assert.equal(fixture.format, 'sheetboard/1');
  assert.match(fixture.rev, /^[a-f0-9]{8}$/);
  assert.equal(Math.floor(Date.parse(fixture.updated) / 1000), fixture.updatedUnix);
  assert.deepEqual(Object.keys(ranges).sort(), ['chips', 'event', 'fitcase', 'wraptest']);
  for (const [id, range] of Object.entries(ranges)) {
    assert.ok(range.rows.length > 0 && range.rows.length <= 24, `${id} row bound`);
    const cols = range.rows[0].length;
    assert.ok(cols > 0 && cols <= 6, `${id} column bound`);
    assert.ok(range.rows.every(row => row.length === cols), `${id} rectangular rows`);
    for (const key of ['style', 'color', 'bg', 'ha', 'wrap', 'size', 'chip']) {
      if (range[key] === undefined) continue;
      assert.equal(range[key].length, range.rows.length, `${id}.${key} row count`);
      assert.ok(range[key].every(row => row.length === cols), `${id}.${key} shape`);
    }
    if (range.colw) assert.equal(range.colw.length, cols, `${id}.colw`);
    if (range.rowh) assert.equal(range.rowh.length, range.rows.length, `${id}.rowh`);
    for (const [r, c, rs, cs] of range.merges || []) {
      assert.ok(r >= 0 && c >= 0 && rs >= 1 && cs >= 1);
      assert.ok(r + rs <= range.rows.length && c + cs <= cols, `${id} merge bounds`);
      for (let y = r; y < r + rs; y++) for (let x = c; x < c + cs; x++) {
        if (y === r && x === c) continue;
        assert.equal(text(range.rows[y][x]), '', `${id} merged covered cell ${y},${x}`);
      }
    }
  }
});

test('event covers merge, blank, color, style and variable-size cases', () => {
  const event = ranges.event;
  assert.equal(event.title, '');
  assert.equal(event.header, false);
  assert.ok(event.merges.length >= 2);
  assert.ok(event.rows.some(row => row.every(value => text(value) === '')));
  assert.ok(event.style.flat().some(value => value !== 0));
  assert.ok(event.style.flat().some(value => value === 0));
  assert.ok(event.bg.flat().some(Boolean) && event.color.flat().some(Boolean));
  assert.ok(new Set(event.rowh).size > 1 && new Set(event.colw).size > 1);
  assert.ok(new Set(event.size.flat()).size > 1);
});

test('wraptest covers overflow, explicit wrapping, alignment, style bits and numeric display text', () => {
  const range = ranges.wraptest;
  assert.ok(text(range.rows[0][0]).length >= 40);
  assert.ok(range.rows[0].slice(1).filter(value => text(value) === '').length >= 2);
  assert.equal(range.wrap[1][0], 1);
  assert.match(text(range.rows[1][0]), /\n/);
  assert.ok(['l', 'c', 'r'].every(align => range.ha.flat().includes(align)));
  for (const bit of [1, 2, 4, 8]) assert.ok(range.style.flat().some(value => (value & bit) !== 0));
  assert.ok(flattened(range.rows).includes('1,234'));
});

test('fitcase keeps the two narrow empty spacer columns and compact full rows', () => {
  const range = ranges.fitcase;
  assert.equal(range.title, '');
  assert.equal(range.header, true);
  assert.equal(range.rows.length, 11);
  assert.ok(range.rows.every(row => row.length === 6 && row[2] === '' && row[4] === ''));
  assert.ok(range.colw[2] < 40 && range.colw[4] < 40);
  assert.ok(range.rowh.every(height => height === 21));
  assert.ok(range.rows[0][0].length >= 8 && range.rows[0][3].length >= 6);
  assert.ok(range.rows.at(-1).some(value => text(value).length > 0));
});

test('chips cover explicit, automatic, blank, normalized-repeat and mixed-width labels', () => {
  const range = ranges.chips;
  const cells = range.rows.flatMap((row, r) => row.map((value, c) => ({value: text(value), marker: range.chip[r][c], size: range.size[r][c], style: range.style[r][c], r, c})));
  const blankish = value => /^[\s\-‐‑‒–—―ー－／/・]+$/.test(value);
  const implicit = cells.filter(cell => cell.marker === 'c');
  const marked = cells.filter(cell => cell.marker === 'c' || /^c\|#[0-9A-F]{6}\|#[0-9A-F]{6}$/i.test(cell.marker));
  assert.ok(marked.some(cell => /^c\|#[0-9A-F]{6}\|#[0-9A-F]{6}$/i.test(cell.marker)), 'explicit chip includes fill and text colors');
  assert.ok(implicit.length >= 30 && cells.length <= 64, 'chip pool remains meaningfully exercised and bounded');
  assert.ok(cells.some(cell => cell.marker === 'a' && cell.value === ''), 'blank cell carries arrow marker');
  assert.ok(implicit.some(cell => blankish(cell.value)), 'dash-like blank value has an implicit chip');
  assert.ok(marked.some(cell => /[ぁ-んァ-ヶ一-龠]/.test(cell.value)), 'Japanese text is shown inside a chip');
  assert.ok(marked.some(cell => /[A-Za-z0-9]/.test(cell.value)), 'ASCII text is shown inside a chip');
  assert.ok(marked.some(cell => /[\uFF01-\uFF60]/.test(cell.value)), 'visible fullwidth ASCII-range characters are shown inside a chip');
  assert.ok(new Set(marked.map(cell => cell.size)).size >= 5, 'marked chips exercise five font sizes');
  assert.ok(marked.some(cell => (cell.style & 1) !== 0) && marked.some(cell => cell.style === 0), 'chips include bold and plain labels');
  assert.ok(new Set(range.rowh).size >= 4, 'chip row heights vary');

  const normalize = value => value.trim().replace(/　/g, '');
  const paletteNames = new Set(implicit.map(cell => normalize(cell.value)).filter(value => value && !blankish(value)));
  assert.ok(paletteNames.size <= 10, `implicit nonblank palette names: ${paletteNames.size}`);
  const positions = new Map();
  for (const cell of implicit) {
    const name = normalize(cell.value);
    if (!name || blankish(name)) continue;
    if (!positions.has(name)) positions.set(name, []);
    positions.get(name).push(cell);
  }
  assert.ok([...positions.values()].some(group => new Set(group.map(cell => cell.r)).size >= 2 && new Set(group.map(cell => cell.c)).size >= 2),
    'a whitespace-normalized implicit label repeats across multiple rows and columns');
  assert.ok([...positions.entries()].some(([name, group]) => name === '種子' && new Set(group.map(cell => cell.value)).size >= 2),
    'distinct raw whitespace variants normalize to the same implicit label');
});
