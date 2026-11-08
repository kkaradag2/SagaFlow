import fs from 'node:fs/promises';
import path from 'node:path';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
const require = createRequire(path.resolve('.artifacts/s25-browser/package.json'));
const { chromium } = require('playwright');
const m = JSON.parse(await fs.readFile(process.argv[2], 'utf8'));
const browser = await chromium.launch({ channel: 'chrome', headless: true });
const context = await browser.newContext({ viewport: { width: 1440, height: 1100 }, permissions: ['clipboard-read', 'clipboard-write'] });
await context.addInitScript(() => localStorage.setItem('sagaflow-dashboard-theme', 'light'));
const connections = [], frames = [], requests = [], errors = [], checks = [];
// Forward every real SignalR message; close one real transport later to exercise reconnect.
await context.routeWebSocket('**/sagaflow/hub?*', socket => {
  const server = socket.connectToServer();
  const connection = { socket, server, closed: false }; connections.push(connection);
  server.onMessage(message => { frames.push(String(message)); socket.send(message); });
  socket.onClose(() => { connection.closed = true; void server.close().catch(() => {}); });
});
const page = await context.newPage(); page.setDefaultTimeout(20000);
page.on('pageerror', error => errors.push(error.message));
page.on('request', request => { if (request.url().includes('/relationships?')) requests.push(request.url()); });
const shot = name => page.screenshot({ path: path.join(m.evidence, name + '.png'), animations: 'disabled' });
const drawer = () => page.getByRole('dialog', { name: 'BigPicture details' });
const list = () => page.getByRole('region', { name: 'Loaded runtime relationships' });
const url = (base, id, identity) => new URL('sagaflow/bigpicture?' + new URLSearchParams({ instanceId: id, ...identity }), base).href;
const wait = async (predicate, label, timeout = 25000) => {
  const until = Date.now() + timeout;
  while (Date.now() < until) { if (await predicate()) return; await new Promise(resolve => setTimeout(resolve, 50)); }
  throw new Error('Timed out: ' + label);
};
const checkpoint = async name => {
  await fs.writeFile(path.join(m.evidence, name + '.signal'), 'Chrome reached checkpoint');
  await wait(async () => { try { await fs.access(path.join(m.evidence, name + '.ack')); return true; } catch { return false; } }, name + ' host action');
};
const openContext = async (base, id, identity) => {
  await page.goto(url(base, id, identity));
  await page.getByRole('region', { name: 'Instance context' }).waitFor();
  await list().waitFor();
};
const selectRow = async prefix => { await list().getByRole('button').filter({ hasText: prefix }).click(); await drawer().waitFor(); };
const close = async () => { await page.getByRole('button', { name: 'Close BigPicture details' }).click(); assert.equal(await drawer().count(), 0); };
const noOverflow = async () => assert.ok(await page.evaluate(() => {
  const panel = document.querySelector('[role="dialog"]');
  return document.documentElement.scrollWidth <= innerWidth && (!panel || panel.scrollWidth <= panel.clientWidth);
}), 'Viewport/Drawer horizontal overflow');
try {
  // Real gRPC discovery + HTTP definition graph. No runtime request without context.
  await page.goto(new URL('sagaflow/bigpicture', m.sourceUrl).href);
  await page.locator('.bigpicture-reference-label').first().waitFor();
  assert.equal(requests.length, 0);
  const related = page.locator('.react-flow__edge.bigpicture-edge-related');
  const invokes = page.locator('.react-flow__edge.bigpicture-edge-invokes');
  assert.equal(await related.count(), 1); assert.equal(await invokes.count(), 2);
  assert.equal(await related.locator('.react-flow__edge-path').evaluate(e => getComputedStyle(e).strokeDasharray), '6px, 4px');
  assert.equal(await page.locator('.bigpicture-reference-label').filter({ hasText: /^invokes$/ }).count(), 2);
  assert.equal(await page.locator('.bigpicture-edge-runtime').count(), 0);
  const declaredInvocation = page.locator('.react-flow__edge.bigpicture-edge-invokes:not(.bigpicture-edge-unresolved) .react-flow__edge-path');
  assert.ok(await declaredInvocation.getAttribute('marker-end'), 'Directed invocation arrow');
  assert.equal(await declaredInvocation.evaluate(element => getComputedStyle(element).stroke), 'rgb(124, 58, 237)');
  const unknown = page.locator('.react-flow__node').filter({ hasText: 'unknown-process' });
  assert.match(await unknown.innerText(), /Unresolved/);
  const resolved = page.locator('.react-flow__node').filter({ hasText: m.target.domain }).filter({ hasText: 'remote' });
  assert.match(await resolved.innerText(), /Remote/); assert.match(await resolved.innerText(), /Resolved/);
  await shot('01-definition-light'); checks.push('real gRPC definition related/invokes/resolved/unresolved; no runtime without context');
  // Existing node -> workflow navigation is still a real browser interaction.
  await resolved.getByRole('button', { name: /^Open workflow for / }).click(); await page.getByRole('region', { name: 'Process Explorer workflow' }).waitFor();
  await page.locator('.workflow-canvas .react-flow__node').first().waitFor();
  await shot('01b-workflow-navigation');
  assert.match(page.url(), /bigpicture\/workflow/);
  await page.getByRole('button', { name: 'Back to Topology' }).click();
  await page.locator('.bigpicture-reference-label').first().waitFor();
  assert.equal(await page.getByRole('button', { name: 'Process Explorer', exact: true }).count(), 1);
  assert.equal(await page.getByRole('button', { name: /^(Workflows?|BigPicture)$/ }).count(), 0);
  await unknown.getByRole('button', { name: /^Open workflow for / }).click();
  await page.getByText(/Workflow definition unavailable/).waitFor();
  assert.equal(await page.locator('.workflow-canvas .react-flow__node').count(), 0);
  await page.getByRole('button', { name: 'Back to Topology' }).click();
  checks.push('unified navigation, published remote workflow, unavailable remote workflow and return to topology');
  // Start from real local Instance Detail, not a constructed DTO.
  await page.goto(new URL('sagaflow/instances?instanceId=' + m.firstId, m.sourceUrl).href);
  await page.getByRole('button', { name: 'View instance in Process Explorer' }).click();
  await list().getByRole('button').nth(1).waitFor();
  assert.equal(new URL(page.url()).searchParams.get('instanceId'), m.firstId);
  assert.ok(requests.some(address => address.includes(m.firstId) && address.includes('domain=' + m.source.domain) && address.includes('limit=100')));
  assert.equal(await list().getByRole('button').count(), 2);
  assert.equal(await page.locator('.react-flow__edge').count(), 3);
  assert.equal(await page.locator('.react-flow__edge.bigpicture-edge-runtime').count(), 1);
  const runtimeEdge = page.locator('.react-flow__edge.bigpicture-edge-runtime');
  await page.locator('.react-flow__node').filter({ has: page.getByText('local', { exact: true }) }).getByRole('button', { name: /^View details for / }).click();
  assert.equal(await drawer().getByRole('heading', { name: 'Process definition', exact: true }).count(), 1);
  assert.equal(await drawer().getByRole('article').count(), 2);
  await shot('02a-source-node-drawer'); await close();
  const runtimeLabel = page.getByRole('button', { name: 'invokes · 2 loaded', exact: true });
  await runtimeLabel.waitFor();
  // Visual regression: later SVG paths must not paint through an earlier relationship label.
  const crossedLabel = await runtimeEdge.evaluate(edge => {
    const label = edge.querySelector('.react-flow__edge-text');
    if (!label) return false; // HTML labels are painted in the dedicated layer above SVG paths.
    const box = label.getBoundingClientRect();
    const edges = [...edge.closest('.react-flow__edges').querySelectorAll('.react-flow__edge')];
    return edges.slice(edges.indexOf(edge) + 1).some(later => {
      const line = later.querySelector('.react-flow__edge-path');
      for (let distance = 0; distance < line.getTotalLength(); distance += 2) {
        const point = line.getPointAtLength(distance).matrixTransform(line.getScreenCTM());
        if (point.x > box.left && point.x < box.right && point.y > box.top && point.y < box.bottom) return true;
      }
      return false;
    });
  });
  assert.equal(crossedLabel, false, 'Visual regression: another SVG edge paints across the runtime relationship label');
  // The visible label is a real accessible control above intersecting SVG paths.
  assert.ok(await runtimeLabel.evaluate(element => {
    const rect = element.getBoundingClientRect();
    return element.contains(document.elementFromPoint(rect.x + rect.width / 2, rect.y + rect.height / 2));
  }), 'Relationship label must be unobscured and clickable');
  assert.equal(await runtimeEdge.locator('.react-flow__edge-path').evaluate(e => getComputedStyle(e).stroke), 'rgb(5, 150, 105)');
  await runtimeLabel.focus(); await page.keyboard.press('Enter'); await drawer().waitFor();
  assert.equal(await drawer().getByRole('article').count(), 2);
  for (const row of m.firstRows) assert.ok((await drawer().innerText()).includes(row.invocationId));
  assert.equal(await drawer().getByText('Accepted', { exact: true }).count(), 1);
  assert.equal(await drawer().getByText('Completed', { exact: true }).count(), 1);
  await shot('02-source-edge-two-invocations');
  await close();
  await selectRow(m.activeInvocationId.slice(0, 8));
  assert.ok((await drawer().innerText()).includes(m.activeTargetId));
  assert.ok((await drawer().innerText()).includes(m.firstRows[1].sourceCorrelationId));
  assert.equal(await drawer().getByText('Last Known Remote Status', { exact: true }).count(), 1);
  assert.equal(await drawer().getByText('Target Local Status', { exact: true }).count(), 0);
  assert.equal(await drawer().getByRole('link').count(), 0);
  await drawer().getByRole('button', { name: 'Copy Invocation ID', exact: true }).click();
  assert.equal(await page.evaluate(() => navigator.clipboard.readText()), m.activeInvocationId);
  await shot('03-source-drawer-light');
  // Zoom, pan and fit while the nonmodal Drawer stays open.
  const viewport = page.locator('.react-flow__viewport').first(); const beforeZoom = await viewport.getAttribute('style');
  await page.getByRole('button', { name: /^zoom in$/i }).click();
  await wait(async () => await viewport.getAttribute('style') !== beforeZoom, 'zoom'); assert.equal(await drawer().count(), 1);
  let lastTransform = '', stableTransform = 0;
  await wait(async () => { const value = await viewport.getAttribute('style'); stableTransform = value === lastTransform ? stableTransform + 1 : 0; lastTransform = value; return stableTransform >= 3; }, 'zoom settling');
  const pane = await page.locator('[data-testid="bigpicture-canvas"]').boundingBox();
  const beforePan = await viewport.getAttribute('style');
  await page.mouse.move(pane.x + 5, pane.y + 5); await page.mouse.down(); await page.mouse.move(pane.x + 65, pane.y + 35, { steps: 8 }); await page.mouse.up();
  assert.notEqual(await viewport.getAttribute('style'), beforePan, 'Actual pan transform');
  assert.equal(await drawer().count(), 1);
  await page.getByRole('button', { name: /^fit view$/i }).click();
  // The overlay intentionally covers the app-header theme button; close/reopen via the real controls.
  await close(); await page.getByRole('button', { name: 'Switch to dark mode' }).click();
  await selectRow(m.activeInvocationId.slice(0, 8)); await shot('04-source-drawer-dark');
  await page.setViewportSize({ width: 390, height: 844 }); await noOverflow(); await shot('05-source-drawer-narrow');
  await page.setViewportSize({ width: 1440, height: 1100 });
  let navigations = 0; const navigated = frame => { if (frame === page.mainFrame()) navigations++; }; page.on('framenavigated', navigated);
  const beforeLive = requests.length;
  await checkpoint('complete');
  await drawer().getByText('Completed', { exact: true }).waitFor();
  assert.ok(requests.length > beforeLive); assert.equal(navigations, 0); assert.equal(await drawer().count(), 1);
  assert.ok(frames.some(frame => frame.includes('SagaInstanceChanged') && frame.includes(m.firstId)));
  await shot('06-live-completed-open-drawer');
  await checkpoint('duplicate');
  await wait(() => requests.length > beforeLive + 1, 'duplicate notification refresh');
  assert.equal(await list().getByRole('button').count(), 2);
  checks.push('real broker completion + SignalR frames/HTTP refresh with open Drawer; no reload; independent invocation isolation');
  // Genuine transport interruption, forwarding all backend messages unchanged.
  const countBeforeReconnect = connections.length, requestsBeforeReconnect = requests.length;
  const active = connections.filter(connection => !connection.closed).at(-1);
  await active.socket.close({ code: 1012, reason: 'Acceptance transient disconnect' }); await active.server.close(); active.closed = true;
  await wait(() => connections.length > countBeforeReconnect, 'SignalR automatic reconnect');
  await wait(() => requests.length > requestsBeforeReconnect, 'reconnect refresh');
  await drawer().getByText('Completed', { exact: true }).waitFor();
  assert.equal(navigations, 0); page.off('framenavigated', navigated);
  checks.push('actual SignalR transport close/reconnect/requery');
  await close();
  await selectRow(m.activeInvocationId.slice(0, 8));
  await drawer().getByRole('button', { name: 'Open local instance' }).click();
  await page.getByRole('button', { name: 'View instance in Process Explorer' }).waitFor();
  await page.goBack(); await list().waitFor(); assert.equal(new URL(page.url()).searchParams.get('instanceId'), m.firstId);
  await page.goForward(); await page.getByRole('button', { name: 'View instance in Process Explorer' }).waitFor();
  // Incoming target is genuine broker state, including independent correlation.
  await page.goto(new URL('sagaflow/instances?instanceId=' + m.activeTargetId, m.targetUrl).href);
  await page.getByRole('region', { name: 'External origin' }).waitFor();
  await page.getByRole('button', { name: 'View instance in Process Explorer' }).click();
  await list().getByRole('button').waitFor();
  assert.match(await list().innerText(), /No matching declared invokes edge/);
  await list().getByRole('button').click();
  assert.equal(await drawer().getByText('Target Local Status', { exact: true }).count(), 1);
  assert.equal(await drawer().getByText('Last Known Remote Status', { exact: true }).count(), 0);
  for (const value of [m.activeTargetId, m.targetRow.sourceSagaInstanceId, m.targetRow.sourceCorrelationId, m.targetRow.targetCorrelationId])
    assert.ok((await drawer().innerText()).includes(value));
  assert.equal(await drawer().getByRole('link').count(), 0); await shot('07-target-incoming-unmatched');
  // Real Prepared request paused before publication: no target ID or observation timestamp exists.
  await openContext(m.sourceUrl, m.metadataId, m.source); await list().getByRole('button').click();
  assert.ok((await drawer().innerText()).includes('Not known / not recorded'));
  assert.equal(await drawer().getByRole('button', { name: 'Copy Target Saga ID', exact: true }).count(), 0);
  assert.equal(await drawer().getByText('Prepared', { exact: true }).count(), 1); await shot('08-prepared-null-metadata');
  // Real local publication failure, followed by real remote completion; sticky reconciliation remains.
  await page.goto(new URL('sagaflow/instances?instanceId=' + m.failedId, m.failedUrl).href);
  await page.getByText('Failed', { exact: true }).first().waitFor();
  await page.getByRole('button', { name: 'View instance in Process Explorer' }).click();
  await list().getByRole('button').click();
  await drawer().getByRole('alert').waitFor(); await checkpoint('reconcile');
  await drawer().getByText('Completed', { exact: true }).waitFor();
  assert.equal(await drawer().getByRole('alert').count(), 1); await shot('09-reconciliation-remote-completed');
  await page.setViewportSize({ width: 390, height: 844 }); await noOverflow(); await shot('10-reconciliation-narrow');
  await drawer().getByRole('button', { name: 'Open local instance' }).scrollIntoViewIfNeeded();
  await noOverflow(); await shot('10b-reconciliation-narrow-scrolled');
  await page.setViewportSize({ width: 1440, height: 1100 });
  // Isolated, seeded high-volume fixture exercises real HTTP pagination, not broker execution.
  await openContext(m.pagesUrl, m.pagesId, m.pages); await page.getByText('Partial results: 100 loaded; more records are available.').waitFor();
  assert.equal(await list().getByRole('button').count(), 100); await shot('11-pagination-partial-fixture');
  const beforeMore = requests.length; await page.getByRole('button', { name: 'Load more relationships' }).click();
  await wait(async () => await list().getByRole('button').count() === 101, 'second real HTTP page');
  assert.ok(requests.slice(beforeMore).some(address => address.includes('offset=100') && address.includes('limit=100')));
  assert.equal(await page.getByRole('button', { name: 'Load more relationships' }).count(), 0);
  assert.equal(new Set(await list().getByRole('button').allTextContents()).size, 101);
  checks.push('separate seeded fixture: real HTTP 100+1 pagination, partial labeling, serial load');
  // Latency injection forwards an actual backend response; no response data is fabricated.
  let release, fetched; const gate = new Promise(resolve => { release = resolve; }); const ready = new Promise(resolve => { fetched = resolve; });
  const delayed = '**/api/instances/' + m.firstId + '/relationships?*';
  await page.route(delayed, async route => { const response = await route.fetch(); fetched(); await gate; try { await route.fulfill({ response }); } catch {} });
  await page.goto(url(m.sourceUrl, m.firstId, m.source)); await ready;
  await page.getByText('Loading instance relationships…').waitFor(); await shot('12-real-request-loading');
  await page.evaluate(address => { history.pushState(null, '', address); dispatchEvent(new PopStateEvent('popstate')); }, url(m.sourceUrl, m.otherId, m.source));
  await list().getByRole('button').waitFor(); release(); await page.unroute(delayed);
  await wait(async () => (await page.getByRole('region', { name: 'Instance context' }).innerText()).includes(m.otherId), 'new instance context');
  assert.equal(await list().getByRole('button').count(), 1);
  assert.ok((await list().innerText()).includes(m.otherRows[0].invocationId.slice(0, 8)));
  assert.ok(!(await list().innerText()).includes(m.activeInvocationId.slice(0, 8)));
  checks.push('real HTTP response latency + fast SPA context switch; stale response isolation');
  await page.goto(new URL('sagaflow/bigpicture', m.targetUrl).href);
  const standalone = page.locator('.react-flow__node');
  await standalone.waitFor();
  assert.equal(await standalone.count(), 1);
  assert.match(await standalone.innerText(), /Local/);
  assert.equal(await page.locator('.react-flow__edge').count(), 0);
  await shot('13a-standalone-local');
  await standalone.getByRole('button', { name: /^Open workflow for / }).click();
  await page.getByRole('region', { name: 'Process Explorer workflow' }).waitFor();
  await page.locator('[data-workflow-kind="step"]').first().waitFor();
  checks.push('standalone Local topology and local workflow');
  await openContext(m.emptyUrl, m.emptyId, m.empty); await page.getByText('No runtime relationships recorded for this local instance.').waitFor(); await shot('13-empty-real-local-saga');
  await page.goto(url(m.sourceUrl, 'unknown-local-instance', m.source)); await page.getByRole('alert').waitFor();
  assert.match(await page.getByRole('alert').innerText(), /404/); await shot('14-real-http-error');
  // Subscription disposal after context is removed: a real host event must not requery this page.
  await openContext(m.sourceUrl, m.firstId, m.source); await list().getByRole('button').nth(1).waitFor();
  await page.getByRole('button', { name: 'Clear instance context' }).click();
  await wait(() => connections.filter(connection => !connection.closed).length === 0, 'subscription cleanup');
  const beforeCleanup = requests.length; await checkpoint('cleanup');
  assert.equal(requests.length, beforeCleanup); assert.equal(await page.getByRole('region', { name: 'Instance context' }).count(), 0);
  await page.locator('.bigpicture-reference-label').first().waitFor();
  await page.setViewportSize({ width: 390, height: 844 });
  await page.getByRole('button', { name: /^fit view$/i }).click();
  await shot('15-definition-narrow');
  await page.getByRole('button', { name: /^zoom in$/i }).click();
  await page.getByRole('button', { name: /^zoom in$/i }).click();
  const mobileCanvas = await page.locator('[data-testid="bigpicture-canvas"]').boundingBox();
  await page.mouse.move(mobileCanvas.x + 6, mobileCanvas.y + 6); await page.mouse.down();
  await page.mouse.move(mobileCanvas.x + 100, mobileCanvas.y + 30, { steps: 8 }); await page.mouse.up();
  await noOverflow(); await shot('15b-definition-narrow-zoom-pan');
  checks.push('history/deep links/local navigation; clipboard; light/dark/390px; error/loading/empty; subscription cleanup');
  assert.deepEqual(errors, []);
  await fs.writeFile(path.join(m.evidence, 'browser-result.json'), JSON.stringify({ status: 'PASS', browser: browser.version(), checks,
    pageErrors: errors, relationshipRequests: requests, signalRMessages: frames.filter(frame => frame.includes('SagaInstanceChanged')).length,
    visualReview: 'Screenshots require separate human/agent inspection; DOM assertions alone are not visual acceptance.' }, null, 2));
  console.log('BIGPICTURE_BROWSER_PASS');
} catch (error) { await shot('failure'); console.error(error); process.exitCode = 1; }
finally { await browser.close(); }
