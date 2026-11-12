import { spawn } from 'node:child_process';
import { createServer } from 'node:http';
import { connect } from 'node:net';
import { dirname, resolve } from 'node:path';

const [executable, ...unityArgs] = process.argv.slice(2);
const serverHost = process.env.ZENITH_LOCAL_HOST?.trim() || '127.0.0.1';
const httpsPort = Number(process.env.ZENITH_LOCAL_HTTPS_PORT ?? '443');
if (!executable || !Number.isInteger(httpsPort) || httpsPort < 1 || httpsPort > 65535) {
    console.error('Usage: node tools/launch-local-client.mjs <UnityClient@Windows.exe> [Unity options]');
    process.exit(2);
}

const localHosts = new Set([
    '4bb7b.playfabapi.com',
    '844ee.playfabapi.com',
    'd4889.playfabapi.com',
    'ramenvr.sfo3.digitaloceanspaces.com',
]);
const sockets = new Set();
const proxy = createServer((_, response) => response.writeHead(405).end());

proxy.on('connection', socket => {
    sockets.add(socket);
    socket.on('close', () => sockets.delete(socket));
    socket.on('error', () => socket.destroy());
});

proxy.on('connect', (request, downstream, head) => {
    const authority = /^([a-z0-9.-]+):443$/i.exec(request.url);
    if (!authority) {
        downstream.end('HTTP/1.1 400 Bad Request\r\n\r\n');
        return;
    }

    const host = authority[1].toLowerCase().replace(/\.$/, '');
    const local = host.endsWith('.zenithmmo.com') || localHosts.has(host);
    if (local) console.log(`Zenith HTTPS ${host} -> ${serverHost}:${httpsPort}`);
    const upstream = connect(local ? httpsPort : 443, local ? serverHost : host);
    let established = false;
    upstream.setTimeout(10_000, () => upstream.destroy());
    upstream.once('connect', () => {
        established = true;
        upstream.setTimeout(0);
        downstream.write('HTTP/1.1 200 Connection Established\r\n\r\n');
        if (head.length) upstream.write(head);
        downstream.pipe(upstream).pipe(downstream);
    });
    upstream.on('error', () => {
        if (established) downstream.destroy();
        else downstream.end('HTTP/1.1 502 Bad Gateway\r\n\r\n');
    });
    upstream.on('close', () => downstream.end());
    downstream.on('close', () => upstream.destroy());
});

proxy.on('error', error => {
    console.error(error.message);
    process.exitCode = 1;
});

proxy.listen(0, '127.0.0.1', () => {
    const clientPath = resolve(executable);
    const child = spawn(clientPath, unityArgs, {
        cwd: dirname(clientPath),
        env: {
            ...process.env,
            HTTPS_PROXY: `http://127.0.0.1:${proxy.address().port}`,
            NO_PROXY: '',
        },
        stdio: 'inherit',
    });

    const close = () => {
        for (const socket of sockets) socket.destroy();
        proxy.close();
    };
    child.once('error', error => {
        console.error(error.message);
        process.exitCode = 1;
        close();
    });
    child.once('exit', code => {
        process.exitCode = code ?? 1;
        close();
    });
    process.once('SIGINT', () => child.kill());
    process.once('SIGTERM', () => child.kill());
});
