// root/tests/check-frontend.mjs
import { readFileSync } from 'node:fs';
import { Script } from 'node:vm';
import { strict as assert } from 'node:assert';

const html = readFileSync('index.html', 'utf8');
const match = html.match(/<script>([\s\S]*?)<\/script>/);
assert.ok(match, 'index.html must contain a JavaScript application');
new Script(match[1], { filename: 'netforge-inline.js' });

const requiredViews = [
  'dashboard', 'wisp', 'customers', 'tickets', 'planner',
  'highsites', 'sectors', 'backhauls', 'finance', 'surveys',
  'workorders', 'inventory', 'scripts', 'settings',
];
for (const view of requiredViews) {
  assert.ok(html.includes("'"+view+"'"), 'Missing navigation entry ' + view);
}

const requiredFunctions = [
  'customer360', 'site360', 'finance', 'wisp',
  'schedule', 'showGlobalSearch', 'exportCollection',
];
for (const func of requiredFunctions) {
  assert.ok(match[1].includes('function ' + func + '('), 'Missing function ' + func);
}

assert.ok(html.includes('api/index.php'), 'Missing PHP API endpoint');
assert.ok(html.includes('X-CSRF-Token'), 'Missing CSRF header');
assert.ok(html.includes('revision'), 'Missing optimistic record revisions');
console.log('NetForge frontend syntax and essential screen checks passed.');
