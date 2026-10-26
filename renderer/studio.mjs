import fs from 'node:fs/promises';
import { openSync, closeSync } from 'node:fs';
import path from 'node:path';
import net from 'node:net';
import { spawn } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { createHash } from 'node:crypto';

const here = path.dirname(fileURLToPath(import.meta.url));
const [directory, session, jobId] = process.argv.slice(2);
const statePath = path.join(session, 'server.json');
// Remotion 4.0.538 scopes staticFile() URLs to the renderer/public-directory pair.
const staticBase = '/static-' + createHash('sha256').update(JSON.stringify([here, path.resolve(directory, 'public')])).digest('hex').slice(0, 12);
await fs.mkdir(session, { recursive: true });
// Serialize launches across MCP clients, without trusting a stale PID for termination.
let lock;
for (let attempt = 0; attempt < 180; attempt++) {
    try {
        lock = await fs.open(path.join(session, 'launch.lock'), 'wx');
        break;
    } catch (error) {
        if (error.code !== 'EEXIST') throw error;
        const stat = await fs.stat(path.join(session, 'launch.lock')).catch(() => null);
        if (stat && Date.now() - stat.mtimeMs > 180000) {
            await fs.unlink(path.join(session, 'launch.lock')).catch(() => {});
        }
        await new Promise((resolve) => setTimeout(resolve, 500));
    }
}
if (!lock) throw Error('Studio launch already in progress');

async function ready(state) {
    if (!Number.isInteger(state.port) || state.port < 1024 || state.port > 65535) return false;
    try {
        const response = await fetch(`http://127.0.0.1:${state.port}${staticBase}/toon-preview-id.json`, { signal: AbortSignal.timeout(1200) });
        return response.ok && (await response.json()).job_id === jobId;
    } catch {
        return false;
    }
}
try {
    const prior = await fs.readFile(statePath, 'utf8').then(JSON.parse).catch(() => null);
    if (prior && await ready(prior)) {
        process.stdout.write(JSON.stringify({ ...prior, reused: true }));
    } else {
        const socket = net.createServer();
        await new Promise((resolve, reject) => { socket.once('error', reject); socket.listen(0, '127.0.0.1', resolve); });
        const port = socket.address().port;
        await new Promise((resolve) => socket.close(resolve));
        await fs.writeFile(path.join(session, 'empty.env'), '');
        const log = openSync(path.join(session, 'studio.log'), 'a');
        const child = spawn(process.execPath, [
            '--require', path.join(here, 'studio-loopback.cjs'),
            path.join(here, 'node_modules/@remotion/cli/remotion-cli.js'), 'studio',
            path.join(here, 'src/index.tsx'), '--no-open', '--force-new',
            '--editor', 'vscode',
            '--port', String(port), '--props', path.join(directory, 'studio-props.json'),
            '--public-dir', path.join(directory, 'public'),
            '--env-file', path.join(session, 'empty.env')
        ], { cwd: here, detached: true, windowsHide: true, stdio: ['ignore', log, log] });
        closeSync(log);
        child.unref();
        let ended = false;
        child.on('error', () => { ended = true; });
        child.on('exit', () => { ended = true; });
        const state = { port, pid: child.pid, url: `http://localhost:${port}/Toon`, static_base: staticBase, log_path: path.join(session, 'studio.log') };
        await fs.writeFile(statePath, JSON.stringify(state, null, 4));
        let loaded = false;
        for (let attempt = 0; attempt < 120 && !ended; attempt++) {
            if (await ready(state)) { loaded = true; break; }
            await new Promise((resolve) => setTimeout(resolve, 500));
        }
        if (!loaded) {
            // This handle belongs to the process created above, never to a PID read from disk.
            child.kill();
            throw Error('Studio startup failed; inspect studio.log');
        }
        process.stdout.write(JSON.stringify({ ...state, reused: false }));
    }
} finally {
    await lock.close();
    await fs.unlink(path.join(session, 'launch.lock'));
}
