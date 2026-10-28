import { createServer, type IncomingMessage, type ServerResponse, type Server } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize, sep } from "node:path";
import { randomInt, randomUUID, timingSafeEqual } from "node:crypto";
import { WebSocketServer, WebSocket, type RawData } from "ws";

/**
 * The relay is deliberately dumb: it authenticates sockets and routes messages.
 * The host mod owns all game rules and re-checks every command against the session
 * token the relay attaches. Protocol: see docs/PROTOCOL.md.
 */

export type Role = "ghost" | "intern" | "admin";
const ROLES: readonly Role[] = ["ghost", "intern", "admin"];

export interface RelayOptions {
  port: number;
  /** Interface to bind. Use 127.0.0.1 behind a reverse proxy so the raw port isn't reachable from outside. */
  host?: string;
  /** If set, the host mod must present this in host.hello (Relay.Secret in the mod config). */
  hostSecret?: string;
  /** Directory served over HTTP (the dev console / web app). */
  publicDir?: string;
  log?: (msg: string) => void;
}

interface Client {
  conn: string;
  ws: WebSocket;
  ip: string;
  lobby?: Lobby;
  role?: Role;
  token?: string;
  joinAttempts: number;
}

interface Lobby {
  code: string;
  hostKey: string;
  host?: WebSocket;
  clients: Map<string, Client>;
  emptySince?: number;
}

const CAMERA_FRAME = 0x01;
const MAX_JOIN_ATTEMPTS = 8;
const LOBBY_TTL_MS = 10 * 60_000;
const CODE_ALPHABET = "23456789ABCDEFGHJKLMNPQRSTUVWXYZ";

const MIME: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".js": "text/javascript; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".json": "application/json",
  ".png": "image/png",
  ".svg": "image/svg+xml",
  ".mp3": "audio/mpeg",
  ".ogg": "audio/ogg",
  ".wav": "audio/wav",
};

export function startRelay(opts: RelayOptions): Promise<{ server: Server; port: number; close: () => Promise<void> }> {
  const log = opts.log ?? ((m: string) => console.log(`[relay] ${m}`));
  const lobbiesByCode = new Map<string, Lobby>();
  const lobbiesByHostKey = new Map<string, Lobby>();

  const server = createServer((req, res) => void serveStatic(req, res, opts.publicDir));
  const hostWss = new WebSocketServer({ noServer: true, maxPayload: 4 * 1024 * 1024 });
  const playWss = new WebSocketServer({ noServer: true, maxPayload: 16 * 1024 });

  server.on("upgrade", (req, socket, head) => {
    const path = new URL(req.url ?? "/", "http://x").pathname;
    const wss = path === "/host" ? hostWss : path === "/play" ? playWss : undefined;
    if (!wss) return socket.destroy();
    wss.handleUpgrade(req, socket, head, (ws) => wss.emit("connection", ws, req));
  });

  // ---------------- host (the BepInEx mod) ----------------

  hostWss.on("connection", (ws: WebSocket) => {
    let lobby: Lobby | undefined;
    const helloTimer = setTimeout(() => ws.close(4000, "Expected host.hello"), 5000);

    ws.on("message", (raw: RawData, isBinary: boolean) => {
      if (isBinary) {
        if (lobby) forwardFrame(lobby, toBuffer(raw));
        return;
      }
      const msg = parse(raw);
      if (!msg) return;

      if (!lobby) {
        clearTimeout(helloTimer);
        if (msg.t !== "host.hello" || typeof msg.hostKey !== "string" || msg.hostKey.length < 16) {
          return ws.close(4000, "Expected host.hello");
        }
        if (opts.hostSecret && !safeEqual(opts.hostSecret, String(msg.secret ?? ""))) {
          send(ws, { t: "error", error: "Bad relay secret" });
          return ws.close(4003, "Bad relay secret");
        }
        lobby = claimLobby(msg.hostKey, ws);
        return;
      }
      onHostMessage(lobby, msg);
    });

    ws.on("close", () => {
      clearTimeout(helloTimer);
      if (!lobby || lobby.host !== ws) return;
      lobby.host = undefined;
      lobby.emptySince = lobby.clients.size === 0 ? Date.now() : undefined;
      broadcast(lobby, () => true, { t: "host.offline" });
      log(`lobby ${lobby.code}: host disconnected`);
    });
  });

  function claimLobby(hostKey: string, ws: WebSocket): Lobby {
    let lobby = lobbiesByHostKey.get(hostKey);
    if (!lobby) {
      lobby = { code: newCode(), hostKey, clients: new Map() };
      lobbiesByCode.set(lobby.code, lobby);
      lobbiesByHostKey.set(hostKey, lobby);
      log(`lobby ${lobby.code}: created`);
    }
    if (lobby.host && lobby.host !== ws) lobby.host.close(4002, "Replaced by a newer host connection");
    lobby.host = ws;
    lobby.emptySince = undefined;
    send(ws, { t: "host.welcome", lobby: lobby.code });
    log(`lobby ${lobby.code}: host connected`);

    // The mod may have restarted (tokens gone) or just reconnected (tokens valid). Let it decide.
    for (const c of lobby.clients.values()) {
      if (c.token) send(ws, { t: "auth.request", reqId: `resume-${c.conn}`, conn: c.conn, token: c.token });
    }
    broadcast(lobby, () => true, { t: "host.online" });
    return lobby;
  }

  function onHostMessage(lobby: Lobby, msg: Record<string, any>) {
    switch (msg.t) {
      case "auth.result": {
        const c = lobby.clients.get(msg.conn);
        if (!c) return;
        if (msg.ok && ROLES.includes(msg.role)) {
          c.role = msg.role;
          c.token = msg.token;
          send(c.ws, { t: "joined", lobby: lobby.code, role: c.role, token: c.token, name: msg.name });
        } else {
          c.role = undefined;
          c.token = undefined;
          send(c.ws, { t: "join.failed", error: String(msg.error ?? "Rejected") });
        }
        return;
      }
      case "view": {
        if (!ROLES.includes(msg.role)) return;
        broadcast(lobby, (c) => c.role === msg.role, { t: "view", data: msg.data });
        return;
      }
      case "event": {
        const roles: Role[] = Array.isArray(msg.roles) ? msg.roles : [];
        broadcast(lobby, (c) => !!c.role && roles.includes(c.role), { t: "event", data: msg.data });
        return;
      }
      case "cmd.result": {
        const c = lobby.clients.get(msg.conn);
        if (c) send(c.ws, { t: "cmd.result", reqId: msg.reqId, ok: !!msg.ok, error: msg.error, data: msg.data });
        return;
      }
      case "kick": {
        const c = lobby.clients.get(msg.conn);
        if (c) {
          c.token = undefined;
          c.role = undefined;
          c.ws.close(4001, "Removed by the host");
        }
        return;
      }
    }
  }

  function forwardFrame(lobby: Lobby, frame: Buffer) {
    if (frame.length < 2 || frame[0] !== CAMERA_FRAME) return;
    for (const c of lobby.clients.values()) {
      if ((c.role === "intern" || c.role === "admin") && c.ws.readyState === WebSocket.OPEN && c.ws.bufferedAmount < 512 * 1024) {
        c.ws.send(frame, { binary: true });
      }
    }
  }

  // ---------------- players (browsers) ----------------

  playWss.on("connection", (ws: WebSocket, req: IncomingMessage) => {
    const client: Client = { conn: randomUUID(), ws, ip: req.socket.remoteAddress ?? "?", joinAttempts: 0 };

    ws.on("message", (raw: RawData, isBinary: boolean) => {
      if (isBinary) return;
      const msg = parse(raw);
      if (!msg) return;

      if (msg.t === "join" || msg.t === "resume") return onJoin(client, msg);

      if (msg.t === "cmd") {
        const lobby = client.lobby;
        if (!lobby || !client.role || !client.token) return send(ws, { t: "cmd.result", reqId: msg.reqId, ok: false, error: "Join first." });
        if (!lobby.host) return send(ws, { t: "cmd.result", reqId: msg.reqId, ok: false, error: "The host is offline." });
        send(lobby.host, {
          t: "cmd",
          reqId: String(msg.reqId ?? ""),
          conn: client.conn,
          token: client.token,
          cmd: String(msg.cmd ?? ""),
          args: typeof msg.args === "object" && msg.args ? msg.args : {},
        });
      }
    });

    ws.on("close", () => {
      const lobby = client.lobby;
      if (!lobby) return;
      lobby.clients.delete(client.conn);
      if (lobby.host) send(lobby.host, { t: "client.left", conn: client.conn });
      if (!lobby.host && lobby.clients.size === 0) lobby.emptySince = Date.now();
    });
  });

  function onJoin(client: Client, msg: Record<string, any>) {
    if (++client.joinAttempts > MAX_JOIN_ATTEMPTS) return client.ws.close(4029, "Too many attempts");

    const code = String(msg.lobby ?? "").trim().toUpperCase();
    const lobby = lobbiesByCode.get(code);
    if (!lobby) return send(client.ws, { t: "join.failed", error: "No lobby with that code." });
    if (!lobby.host) return send(client.ws, { t: "join.failed", error: "The host is offline." });

    if (client.lobby && client.lobby !== lobby) client.lobby.clients.delete(client.conn);
    client.lobby = lobby;
    lobby.clients.set(client.conn, client);
    lobby.emptySince = undefined;

    send(lobby.host, msg.t === "resume"
      ? { t: "auth.request", reqId: client.conn, conn: client.conn, token: String(msg.token ?? "") }
      : {
          t: "auth.request",
          reqId: client.conn,
          conn: client.conn,
          role: String(msg.role ?? ""),
          pin: String(msg.pin ?? ""),
          name: String(msg.name ?? "").slice(0, 24),
        });
  }

  // ---------------- plumbing ----------------

  function broadcast(lobby: Lobby, filter: (c: Client) => boolean, msg: object) {
    const data = JSON.stringify(msg);
    for (const c of lobby.clients.values()) {
      if (filter(c) && c.ws.readyState === WebSocket.OPEN) c.ws.send(data);
    }
  }

  function newCode(): string {
    for (;;) {
      let code = "";
      for (let i = 0; i < 5; i++) code += CODE_ALPHABET[randomInt(CODE_ALPHABET.length)];
      if (!lobbiesByCode.has(code)) return code;
    }
  }

  const heartbeat = setInterval(() => {
    for (const wss of [hostWss, playWss]) {
      for (const ws of wss.clients) {
        if ((ws as any).dolAlive === false) { ws.terminate(); continue; }
        (ws as any).dolAlive = false;
        ws.ping();
      }
    }
    const now = Date.now();
    for (const lobby of lobbiesByCode.values()) {
      if (lobby.emptySince && now - lobby.emptySince > LOBBY_TTL_MS) {
        lobbiesByCode.delete(lobby.code);
        lobbiesByHostKey.delete(lobby.hostKey);
        log(`lobby ${lobby.code}: expired`);
      }
    }
  }, 20_000);
  for (const wss of [hostWss, playWss]) {
    wss.on("connection", (ws: WebSocket) => ws.on("pong", () => ((ws as any).dolAlive = true)));
  }

  return new Promise((resolve) => {
    server.listen(opts.port, opts.host, () => {
      const address = server.address();
      const port = typeof address === "object" && address ? address.port : opts.port;
      resolve({
        server,
        port,
        close: () =>
          new Promise<void>((done) => {
            clearInterval(heartbeat);
            for (const wss of [hostWss, playWss]) for (const ws of wss.clients) ws.terminate();
            server.close(() => done());
          }),
      });
    });
  });
}

function send(ws: WebSocket, msg: object) {
  if (ws.readyState === WebSocket.OPEN) ws.send(JSON.stringify(msg));
}

function parse(raw: RawData): Record<string, any> | undefined {
  try {
    const value = JSON.parse(toBuffer(raw).toString("utf8"));
    return value && typeof value === "object" && !Array.isArray(value) ? value : undefined;
  } catch {
    return undefined;
  }
}

function toBuffer(raw: RawData): Buffer {
  return Buffer.isBuffer(raw) ? raw : Array.isArray(raw) ? Buffer.concat(raw) : Buffer.from(raw as ArrayBuffer);
}

function safeEqual(a: string, b: string): boolean {
  const x = Buffer.from(a);
  const y = Buffer.from(b);
  return x.length === y.length && timingSafeEqual(x, y);
}

async function serveStatic(req: IncomingMessage, res: ServerResponse, dir?: string) {
  const path = new URL(req.url ?? "/", "http://x").pathname;
  if (path === "/healthz") {
    res.writeHead(200, { "content-type": "text/plain" }).end("ok");
    return;
  }
  if (!dir || req.method !== "GET") {
    res.writeHead(404).end();
    return;
  }
  const file = normalize(join(dir, path === "/" ? "index.html" : decodeURIComponent(path)));
  if (!file.startsWith(normalize(dir) + sep)) {
    res.writeHead(403).end();
    return;
  }
  try {
    const body = await readFile(file);
    res.writeHead(200, { "content-type": MIME[extname(file)] ?? "application/octet-stream" }).end(body);
  } catch {
    res.writeHead(404).end();
  }
}
