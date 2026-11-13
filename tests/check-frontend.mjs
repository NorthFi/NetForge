// root/tests/check-frontend.mjs
import { readFileSync } from 'node:fs';
import { Script } from 'node:vm';
import { strict as assert } from 'node:assert';

const html = readFileSync('index.html', 'utf8');
const js = readFileSync('assets/netforge.js', 'utf8');
const css = readFileSync('assets/netforge.css', 'utf8');

assert.ok(html.includes('src="assets/netforge.js"'), 'Missing bundled app script');
assert.ok(html.includes('href="assets/netforge.css"'), 'Missing theme stylesheet');
assert.ok(!/<script\s*>/i.test(html), 'Do not use inline JavaScript');
new Script(js, { filename: 'assets/netforge.js' });

const views = ['dashboard', 'wisp', 'customers', 'tickets', 'planner', 'highsites',
  'sectors', 'backhauls', 'finance', 'billing', 'debtors', 'reports', 'usage', 'surveys', 'workorders', 'inventory', 'scripts', 'settings'];
for (const view of views) assert.ok(js.includes("'" + view + "'"), 'Missing navigation: ' + view);
const features = ['customer360', 'site360', 'ticket360', 'finance', 'billingCycles', 'debtors', 'reports', 'inventoryManager', 'inventoryDetail', 'wisp', 'schedule', 'showGlobalSearch', 'exportCollection'];
for (const name of features) assert.ok(js.includes('function ' + name + '('), 'Missing function: ' + name);
for (const selector of ['.shell', '.nf-nav-section', '.nf-status', '.card'])
  assert.ok(css.includes(selector), 'Missing selector: ' + selector);
assert.ok(js.includes('api/index.php'), 'Missing API URL');
assert.ok(js.includes('X-CSRF-Token'), 'Missing CSRF header');
assert.ok(js.includes('revision'), 'Missing optimistic revisions');
console.log('NetForge frontend syntax and screen checks passed.');
