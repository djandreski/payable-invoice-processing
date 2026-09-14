import { spawn } from 'node:child_process';
import { mkdir, readFile, rm, writeFile } from 'node:fs/promises';
import { join } from 'node:path';
import { normalizeAndValidateContract } from './openapi-contract.mjs';

const root = process.cwd();
const openApiPath = join(root, 'src', 'api', 'openapi.json');
const clientPath = join(root, 'src', 'invoice-review-client', 'src', 'api', 'generated', 'client.ts');
const temporaryOpenApi = `${openApiPath}.tmp`;
const temporaryClient = `${clientPath}.tmp`;
const nswagRoot = process.env.USERPROFILE ?? process.env.HOME;
if (!nswagRoot) throw new Error('Unable to locate the NuGet package cache.');
const nswag = join(nswagRoot, '.nuget', 'packages', 'nswag.msbuild', '14.7.1', 'tools', 'Net100', 'dotnet-nswag.dll');
const port = '5190';
const output = [];

function run(command, args, options = {}) {
  return new Promise((resolve, reject) => {
    const child = spawn(command, args, { cwd: root, stdio: ['ignore', 'pipe', 'pipe'], ...options });
    child.stdout.on('data', data => output.push(data.toString()));
    child.stderr.on('data', data => output.push(data.toString()));
    child.on('error', reject);
    child.on('exit', code => code === 0 ? resolve() : reject(new Error(`${command} failed with exit code ${code}:\n${output.join('')}`)));
  });
}

const api = spawn('dotnet', ['run', '--no-build', '--project', 'src/InvoiceReviewAssistant.Api', '--urls', `http://127.0.0.1:${port}`], {
  cwd: root,
  env: { ...process.env, INVOICE_REVIEW_CONTRACT_GENERATION: 'true', ASPNETCORE_ENVIRONMENT: 'Production' },
  stdio: 'ignore',
});

try {
  let rawDocument;
  for (let attempt = 0; attempt < 50; attempt += 1) {
    try {
      const response = await fetch(`http://127.0.0.1:${port}/openapi/v1.json`);
      if (response.ok) { rawDocument = await response.text(); break; }
    } catch { /* Host is still starting. */ }
    await new Promise(resolve => setTimeout(resolve, 100));
  }
  if (!rawDocument) throw new Error('Contract-generation host did not expose OpenAPI.');

  const document = `${JSON.stringify(normalizeAndValidateContract(JSON.parse(rawDocument)), null, 2)}\n`;

  await mkdir(join(root, 'src', 'api'), { recursive: true });
  const previousOpenApi = await readFile(openApiPath, 'utf8').catch(() => null);
  await rm(temporaryOpenApi, { force: true });
  await writeFile(temporaryOpenApi, document);
  await mkdir(join(root, 'src', 'invoice-review-client', 'src', 'api', 'generated'), { recursive: true });
  await rm(temporaryClient, { force: true });
  await run('dotnet', [
    nswag,
    'openapi2tsclient',
    `/input:${temporaryOpenApi}`,
    `/output:${temporaryClient}`,
    '/template:Fetch',
    '/typeStyle:Interface',
    '/dateTimeType:string',
    '/nullValue:Null',
    '/useLeafType:true',
  ]);
  const generated = (await readFile(temporaryClient, 'utf8'))
    .replace(/[ \t]+$/gm, '')
    .replace(/\r?\n/g, '\n');
  const previousClient = await readFile(clientPath, 'utf8').catch(() => null);
  if (process.argv.includes('--check') && (previousOpenApi !== document || previousClient !== generated)) {
    throw new Error('OpenAPI or generated TypeScript client is out of date. Run npm run contracts:generate.');
  }
  await writeFile(openApiPath, document);
  await writeFile(clientPath, generated);
} finally {
  if (api.exitCode === null) {
    await new Promise(resolve => {
      const timeout = setTimeout(resolve, 5000);
      api.once('exit', () => {
        clearTimeout(timeout);
        resolve();
      });
      api.kill();
    });
  }
  await rm(temporaryOpenApi, { force: true });
  await rm(temporaryClient, { force: true });
}
