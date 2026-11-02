// Builds the seed of the centres of the comuni (Casazen.Infrastructure/Data/Seeds/comuni-centroidi.csv and its .source.json).
//
//   node scripts/build-comuni-centroidi.mjs <wikidata.csv> <comuni-istat.csv> <seeds folder>
//
// <wikidata.csv> is the answer of https://query.wikidata.org/sparql to scripts/comuni-centroidi.rq asked as text/csv (columns istat,coord,label).
// <comuni-istat.csv> is the official list of the comuni of the repo (Data/Seeds/comuni-istat.csv). Both are data of a third party: nothing
// in them is executed, every row is checked, and the build stops (exit 1) at the first thing that looks wrong. Runbook: docs/runbooks/comuni-istat.md.
import { createHash } from 'node:crypto';
import { readFileSync, writeFileSync } from 'node:fs';
import { join } from 'node:path';

const [wikidataPath, istatPath, outDir] = process.argv.slice(2);
if (!wikidataPath || !istatPath || !outDir) {
  console.error('usage: node scripts/build-comuni-centroidi.mjs <wikidata.csv> <comuni-istat.csv> <seeds folder>');
  process.exit(2);
}

// Italy with a margin (Campione d'Italia, Lampedusa, Bardonecchia, Otranto, Vetta d'Italia).
const BOX = { latMin: 35.3, latMax: 47.2, lonMin: 6.4, lonMax: 18.9 };
const POINT = /^Point\((-?\d{1,3}(?:\.\d+)?) (-?\d{1,3}(?:\.\d+)?)\)$/;
// Two positions of the same code that are further apart than this are not the same place: somebody has to look.
const MAX_SPREAD_KM = 3;
// Islands far from the other comuni of their province: expected, not an error.
const FAR_BY_NATURE = new Set(['Lampedusa e Linosa', 'Pantelleria']);

const fail = (message) => {
  console.error(`FAILED: ${message}`);
  process.exit(1);
};

const sha256 = (value) => createHash('sha256').update(value).digest('hex');
const rad = (d) => (d * Math.PI) / 180;
const km = (a, b) => {
  const h = Math.sin(rad(b.lat - a.lat) / 2) ** 2 + Math.cos(rad(a.lat)) * Math.cos(rad(b.lat)) * Math.sin(rad(b.lon - a.lon) / 2) ** 2;
  return 2 * 6371 * Math.asin(Math.sqrt(h));
};
const round4 = (value) => Math.round(value * 10000) / 10000;
const norm = (text) =>
  text
    .normalize('NFD')
    .replace(/[̀-ͯ]/g, '')
    .toLowerCase()
    .replace(/[’'`]/g, '')
    .replace(/[^a-z0-9]+/g, ' ')
    .trim();

// A CSV read as a whole: a cell of the header of the ISTAT file has a line break inside its quotes.
function parseCsv(text, separator) {
  const rows = [];
  let row = [];
  let cell = '';
  let quoted = false;
  for (let i = 0; i < text.length; i++) {
    const ch = text[i];
    if (quoted) {
      if (ch === '"' && text[i + 1] === '"') { cell += '"'; i++; }
      else if (ch === '"') quoted = false;
      else cell += ch;
    } else if (ch === '"') quoted = true;
    else if (ch === separator) { row.push(cell); cell = ''; }
    else if (ch === '\n' || ch === '\r') {
      if (ch === '\r' && text[i + 1] === '\n') i++;
      row.push(cell);
      cell = '';
      if (row.some((c) => c.length > 0)) rows.push(row);
      row = [];
    } else cell += ch;
  }
  row.push(cell);
  if (row.some((c) => c.length > 0)) rows.push(row);
  return rows;
}

// UTF-8 when it is, else Windows-1252 (the ISTAT file has been both); a leading byte-order mark is dropped.
function decode(buffer) {
  let text;
  try {
    text = new TextDecoder('utf-8', { fatal: true }).decode(buffer);
  } catch {
    text = new TextDecoder('windows-1252').decode(buffer);
  }
  return text.replace(/^﻿/, '');
}

/* ----- what Wikidata says ----- */

const wikidataBytes = readFileSync(wikidataPath);
const wikidataRows = parseCsv(decode(wikidataBytes), ',');
if (wikidataRows[0]?.join(',') !== 'istat,coord,label') fail(`unexpected header of the Wikidata file: ${wikidataRows[0]?.join(',')}`);

const byCode = new Map();
for (const [istat, coord, label = ''] of wikidataRows.slice(1)) {
  const m = POINT.exec(coord ?? '');
  if (!/^\d{6}$/.test(istat ?? '') || !m) fail(`row of Wikidata that is not an ISTAT code and a point: ${istat} ${coord}`);
  const point = { lon: Number(m[1]), lat: Number(m[2]) };
  if (!(point.lat >= BOX.latMin && point.lat <= BOX.latMax && point.lon >= BOX.lonMin && point.lon <= BOX.lonMax)) fail(`${istat} ${label} is outside Italy: ${coord}`);
  const entry = byCode.get(istat) ?? { points: [], labels: new Set() };
  entry.points.push(point);
  if (label) entry.labels.add(label);
  byCode.set(istat, entry);
}

const centres = new Map();
for (const [code, { points, labels }] of byCode) {
  const spread = Math.max(...points.flatMap((a) => points.map((b) => km(a, b))));
  if (spread > MAX_SPREAD_KM) fail(`${code} ${[...labels].join('/')} has positions ${spread.toFixed(1)} km apart`);
  centres.set(code, {
    lat: round4(points.reduce((sum, p) => sum + p.lat, 0) / points.length),
    lon: round4(points.reduce((sum, p) => sum + p.lon, 0) / points.length),
    labels: [...labels],
  });
}

/* ----- the official list of the repo ----- */

const istatBytes = readFileSync(istatPath);
const istatRows = parseCsv(decode(istatBytes), ';');
const header = istatRows[0].map((h) => h.replace(/\s+/g, ' ').trim());
const column = (name) => {
  const index = header.indexOf(name);
  if (index < 0) fail(`column '${name}' not found in the ISTAT file: ${header.join(' | ')}`);
  return index;
};
const [codeCol, nameCol, bilingualCol] = [column('Codice Comune formato alfanumerico'), column('Denominazione in italiano'), column('Denominazione (Italiana e straniera)')];
const comuni = istatRows.slice(1).map((c) => ({ code: c[codeCol].trim(), name: c[nameCol].trim(), bilingual: c[bilingualCol].trim() }));
if (comuni.length < 7000) fail(`the ISTAT list has only ${comuni.length} rows`);

/* ----- the match: by code, then (for the codes that ISTAT has not published yet) by name ----- */

const seed = new Map();
const missing = [];
const byName = [];
const known = new Set(comuni.map((c) => c.code));
const spare = [...centres].filter(([code]) => !known.has(code));
const spareByName = new Map();
for (const [code, centre] of spare) {
  for (const label of centre.labels) spareByName.set(norm(label), [...(spareByName.get(norm(label)) ?? []), code]);
}

for (const comune of comuni) {
  const direct = centres.get(comune.code);
  if (direct) {
    seed.set(comune.code, direct);
    continue;
  }
  // Sardinia has new provinces since 2025: Wikidata already carries the new codes, the official list still the old ones. Both are written, with
  // the same centre, so a property that holds either of them is found.
  const candidates = new Set([...(spareByName.get(norm(comune.name)) ?? []), ...(spareByName.get(norm(comune.bilingual.split('/')[0])) ?? [])]);
  if (candidates.size === 1) {
    const [other] = [...candidates];
    const centre = centres.get(other);
    seed.set(comune.code, centre);
    seed.set(other, centre);
    byName.push(`${comune.code} ${comune.name} = ${other}`);
  } else {
    missing.push(`${comune.code} ${comune.name}${candidates.size > 1 ? ` (ambiguous: ${[...candidates].join(' ')})` : ''}`);
  }
}
// The codes of Wikidata that match no comune of the list are comuni that no longer exist (merged or suppressed: Wikidata keeps them) or that
// ISTAT has not listed yet. They are left out, so that the seed holds only comuni that a host can choose today; a new comune gets its centre
// when the seed is built again (see the runbook).
const ignored = spare.filter(([code]) => !seed.has(code));

/* ----- checks ----- */

const byProvince = new Map();
for (const [code, centre] of seed) byProvince.set(code.slice(0, 3), [...(byProvince.get(code.slice(0, 3)) ?? []), { code, ...centre }]);
const median = (values) => [...values].sort((a, b) => a - b)[Math.floor(values.length / 2)];
const nameOf = new Map(comuni.map((c) => [c.code, c.name]));
const outliers = [];
for (const members of byProvince.values()) {
  const middle = { lat: median(members.map((m) => m.lat)), lon: median(members.map((m) => m.lon)) };
  for (const m of members) {
    const distance = km(middle, m);
    const name = nameOf.get(m.code) ?? m.labels[0] ?? '?';
    if (distance > 110 && !FAR_BY_NATURE.has(name)) outliers.push(`${m.code} ${name} ${Math.round(distance)} km`);
  }
}
if (outliers.length > 0) fail(`comuni far from the others of their province: ${outliers.join('; ')}`);

// Well-known places, by name: a latitude and a longitude that were swapped, or a wrong sign, cannot pass.
const KNOWN = [
  ['Roma', 41.9, 12.5], ['Milano', 45.46, 9.19], ['Napoli', 40.85, 14.27], ['Torino', 45.07, 7.69], ['Palermo', 38.12, 13.36],
  ['Genova', 44.41, 8.93], ['Bologna', 44.49, 11.34], ['Firenze', 43.77, 11.26], ['Venezia', 45.44, 12.33], ['Como', 45.81, 9.09],
  ['Cagliari', 39.22, 9.12], ['Bari', 41.12, 16.87], ['Catania', 37.5, 15.09], ['Verona', 45.44, 10.99], ['Lecce', 40.35, 18.17],
  ['Bolzano', 46.5, 11.35], ['Olbia', 40.92, 9.5], ['Alghero', 40.56, 8.32], ['Arzachena', 41.08, 9.39], ['Taormina', 37.85, 15.29],
  ['Positano', 40.63, 14.49], ['Capri', 40.55, 14.24], ['Sorrento', 40.63, 14.38], ["Cortina d'Ampezzo", 46.54, 12.14], ['Rimini', 44.06, 12.57],
  ['Ostuni', 40.73, 17.58], ['Polignano a Mare', 41.0, 17.22], ['Cervia', 44.26, 12.35], ['Courmayeur', 45.8, 6.97], ['Portofino', 44.3, 9.21],
  ['Taggia', 43.85, 7.85], ['Cogne', 45.61, 7.36], ['Pantelleria', 36.83, 11.95], ['Lampedusa e Linosa', 35.5, 12.61],
];
for (const [name, lat, lon] of KNOWN) {
  const matches = comuni.filter((c) => norm(c.name) === norm(name));
  if (matches.length !== 1) fail(`${name}: ${matches.length} comuni of that name in the ISTAT list`);
  const centre = seed.get(matches[0].code);
  if (!centre) fail(`${name} (${matches[0].code}) has no centre`);
  if (Math.abs(centre.lat - lat) > 0.12 || Math.abs(centre.lon - lon) > 0.12) fail(`${name} is at ${centre.lat}, ${centre.lon}, expected about ${lat}, ${lon}`);
}

/* ----- the seed ----- */

const lines = ['istat_code,latitude,longitude', ...[...seed].sort(([a], [b]) => (a < b ? -1 : 1)).map(([code, c]) => `${code},${c.lat.toFixed(4)},${c.lon.toFixed(4)}`)];
const csv = `${lines.join('\n')}\n`;
const covered = comuni.length - missing.length;
const metadata = {
  source: 'Wikidata (CC0 1.0): comuni of Italy with an ISTAT code (P635) and a coordinate (P625); the centre of the comune as Wikidata gives it, to four decimals',
  license: 'CC0 1.0',
  endpoint: 'https://query.wikidata.org/sparql',
  query: 'scripts/comuni-centroidi.rq',
  builtBy: 'scripts/build-comuni-centroidi.mjs',
  retrievedAt: new Date().toISOString().slice(0, 10),
  wikidataResponseSha256: sha256(wikidataBytes),
  istatListSha256: sha256(istatBytes),
  rows: seed.size,
  comuniOfTheIstatList: comuni.length,
  comuniWithACentre: covered,
  comuniMatchedByName: byName.length,
  comuniWithoutACentre: missing.length,
  codesOfWikidataLeftOut: ignored.length,
  seedSha256: sha256(csv),
};
writeFileSync(join(outDir, 'comuni-centroidi.csv'), csv, 'utf8');
writeFileSync(join(outDir, 'comuni-centroidi.source.json'), `${JSON.stringify(metadata, null, 2)}\n`, 'utf8');

console.log(`ISTAT list: ${comuni.length} comuni; with a centre: ${covered} (${((covered / comuni.length) * 100).toFixed(1)} %): ${comuni.length - missing.length - byName.length} by code, ${byName.length} by name`);
console.log(`rows of the seed: ${seed.size} (${byName.length} of them are the new codes of Sardinia, written next to the old ones)`);
console.log(`codes of Wikidata left out, no comune of the list (${ignored.length}): ${ignored.map(([code, c]) => `${code} ${c.labels.join('/')}`).join('; ')}`);
console.log(`without a centre (${missing.length}): ${missing.join('; ')}`);
console.log(`by name: ${byName.join('; ')}`);
console.log(JSON.stringify(metadata, null, 2));
