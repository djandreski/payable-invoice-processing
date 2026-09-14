import test from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { acceptedRouteKeys, normalizeAndValidateContract } from './openapi-contract.mjs';

const snapshot = JSON.parse(await readFile(new URL('../src/api/openapi.json', import.meta.url), 'utf8'));

test('the committed snapshot contains only the ten accepted invoice routes', () => {
  const routes = Object.entries(snapshot.paths).flatMap(([path, pathItem]) =>
    Object.keys(pathItem).map(method => `${method.toUpperCase()} ${path}`));
  assert.deepEqual(routes.sort(), [...acceptedRouteKeys].sort());
});

test('the committed snapshot passes the complete contract baseline', () => {
  assert.doesNotThrow(() => normalizeAndValidateContract(structuredClone(snapshot)));
});

test('the baseline rejects route drift', () => {
  const changed = structuredClone(snapshot);
  delete changed.paths['/api/invoices/{id}/approve'];
  assert.throws(() => normalizeAndValidateContract(changed), /HTTP route set/);
});

test('the baseline rejects schema drift after normalization', () => {
  const changed = structuredClone(snapshot);
  delete changed.components.schemas.InvoiceDetailDto;
  assert.throws(() => normalizeAndValidateContract(changed));
});
