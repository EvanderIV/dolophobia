import { test, before, after } from "node:test";
import assert from "node:assert/strict";
import { WebSocket } from "ws";
import { startRelay } from "../src/relay.ts";

let relay: Awaited<ReturnType<typeof startRelay>>;
let base: string;

before(async () => {
  relay = await startRelay({ port: 0, hostSecret: "s3cret", log: () => {} });
  base = `ws://127.0.0.1:${relay.port}`;
});
after(() => relay.close());

/** A socket that queues every message so tests can await them in order. */
class Peer {
  ws: WebSocket;
  private queue: any[] = [];
  private waiters: ((m: any) => void)[] = [];
  closed: Promise<{ code: number; reason: string }>;

  constructor(url: string) {
    this.ws = new WebSocket(url);
    this.ws.on("message", (raw, isBinary) => {
      const msg = isBinary ? { binary: Buffer.from(raw as Buffer) } : JSON.parse(raw.toString());
      const waiter = this.waiters.shift();
      waiter ? waiter(msg) : this.queue.push(msg);
    });
    this.closed = new Promise((r) => this.ws.on("close", (code, reason) => r({ code, reason: reason.toString() })));
  }
  open() { return new Promise<void>((r) => this.ws.once("open", () => r())); }
  send(msg: object) { this.ws.send(JSON.stringify(msg)); }
  next(): Promise<any> {
    const queued = this.queue.shift();
    if (queued) return Promise.resolve(queued);
    return new Promise((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("timed out waiting for message")), 2000);
      this.waiters.push((m) => { clearTimeout(timer); resolve(m); });
    });
  }
  async expect(t: string) {
    const msg = await this.next();
    assert.equal(msg.t, t, `expected ${t}, got ${JSON.stringify(msg)}`);
    return msg;
  }
  close() { this.ws.close(); }
}

const HOST_KEY = "0123456789abcdef0123";

async function connectHost(key = HOST_KEY) {
  const host = new Peer(`${base}/host`);
  await host.open();
  host.send({ t: "host.hello", hostKey: key, secret: "s3cret", version: "test" });
  const welcome = await host.expect("host.welcome");
  return { host, lobby: welcome.lobby as string };
}

/** Joins a player, playing the mod's side of the auth handshake. */
async function join(host: Peer, lobby: string, role: string, pin = "PIN") {
  const player = new Peer(`${base}/play`);
  await player.open();
  player.send({ t: "join", lobby, role, pin, name: `${role}-player` });
  const req = await host.expect("auth.request");
  assert.equal(req.role, role);
  assert.equal(req.pin, pin);
  host.send({ t: "auth.result", reqId: req.reqId, conn: req.conn, ok: true, token: `tok-${role}`, role, name: req.name });
  const joined = await player.expect("joined");
  assert.equal(joined.role, role);
  return { player, conn: req.conn as string };
}

test("rejects a host with the wrong secret", async () => {
  const host = new Peer(`${base}/host`);
  await host.open();
  host.send({ t: "host.hello", hostKey: "ffffffffffffffffffff", secret: "nope" });
  assert.equal((await host.expect("error")).error, "Bad relay secret");
  assert.equal((await host.closed).code, 4003);
});

test("routes views and events by role, commands and results by connection", async () => {
  const { host, lobby } = await connectHost();
  const ghost = await join(host, lobby, "ghost");
  const intern = await join(host, lobby, "intern");

  // Views only reach their role.
  host.send({ t: "view", role: "ghost", data: { blips: [1] } });
  host.send({ t: "view", role: "intern", data: { averageSanity: 73 } });
  assert.deepEqual((await ghost.player.expect("view")).data, { blips: [1] });
  assert.deepEqual((await intern.player.expect("view")).data, { averageSanity: 73 });

  // Events reach every listed role.
  host.send({ t: "event", roles: ["intern"], data: { kind: "motion", room: "Kitchen" } });
  host.send({ t: "event", roles: ["ghost", "intern"], data: { kind: "hunt", active: true } });
  assert.equal((await intern.player.expect("event")).data.kind, "motion");
  assert.equal((await intern.player.expect("event")).data.kind, "hunt");
  assert.equal((await ghost.player.expect("event")).data.kind, "hunt"); // never saw "motion"

  // Commands carry the relay-held token and connection id to the host; results go back to the sender only.
  ghost.player.send({ t: "cmd", reqId: "r1", cmd: "ghost.event", args: { kind: "door" } });
  const cmd = await host.expect("cmd");
  assert.deepEqual(
    { conn: cmd.conn, token: cmd.token, cmd: cmd.cmd, args: cmd.args },
    { conn: ghost.conn, token: "tok-ghost", cmd: "ghost.event", args: { kind: "door" } },
  );
  host.send({ t: "cmd.result", reqId: "r1", conn: ghost.conn, ok: false, error: "Recovering... 5s" });
  assert.deepEqual(await ghost.player.expect("cmd.result"), { t: "cmd.result", reqId: "r1", ok: false, error: "Recovering... 5s" });

  // Camera frames go to interns (and admins), never the ghost.
  const frame = Buffer.from([0x01, 0x00, 0xff, 0xd8, 0xff]);
  host.ws.send(frame, { binary: true });
  assert.deepEqual((await intern.player.next()).binary, frame);

  host.send({ t: "view", role: "ghost", data: "after-frame" });
  assert.equal((await ghost.player.expect("view")).data, "after-frame");

  // Leaving tells the host.
  intern.player.close();
  assert.equal((await host.expect("client.left")).conn, intern.conn);

  ghost.player.close();
  await host.expect("client.left");
  host.close();
});

test("unauthenticated players can't send commands, and failed joins don't grant a role", async () => {
  const { host, lobby } = await connectHost("aaaaaaaaaaaaaaaaaaaa");
  const p = new Peer(`${base}/play`);
  await p.open();

  p.send({ t: "cmd", reqId: "x", cmd: "ghost.hunt" });
  assert.equal((await p.expect("cmd.result")).error, "Join first.");

  p.send({ t: "join", lobby, role: "ghost", pin: "WRONG" });
  const req = await host.expect("auth.request");
  host.send({ t: "auth.result", reqId: req.reqId, conn: req.conn, ok: false, error: "Wrong PIN." });
  assert.equal((await p.expect("join.failed")).error, "Wrong PIN.");

  p.send({ t: "cmd", reqId: "y", cmd: "ghost.hunt" });
  assert.equal((await p.expect("cmd.result")).error, "Join first.");

  p.send({ t: "join", lobby: "NOPE1", role: "ghost", pin: "x" });
  assert.equal((await p.expect("join.failed")).error, "No lobby with that code.");

  p.close();
  host.close();
});

test("host reconnect keeps the lobby code and asks the mod to resume existing sessions", async () => {
  const key = "bbbbbbbbbbbbbbbbbbbb";
  const first = await connectHost(key);
  const intern = await join(first.host, first.lobby, "intern");

  first.host.close();
  await intern.player.expect("host.offline");

  const second = await connectHost(key);
  assert.equal(second.lobby, first.lobby);
  const resume = await second.host.expect("auth.request");
  assert.deepEqual({ conn: resume.conn, token: resume.token }, { conn: intern.conn, token: "tok-intern" });
  await intern.player.expect("host.online");

  // Mod restarted and forgot the token: player is told to rejoin and loses its role.
  second.host.send({ t: "auth.result", reqId: resume.reqId, conn: resume.conn, ok: false, error: "Session expired. Rejoin with a PIN." });
  assert.equal((await intern.player.expect("join.failed")).error, "Session expired. Rejoin with a PIN.");

  intern.player.close();
  second.host.close();
});

test("a kicked player is disconnected", async () => {
  const { host, lobby } = await connectHost("cccccccccccccccccccc");
  const ghost = await join(host, lobby, "ghost");
  host.send({ t: "kick", conn: ghost.conn });
  assert.equal((await ghost.player.closed).code, 4001);
  host.close();
});
