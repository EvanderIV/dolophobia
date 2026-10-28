import { fileURLToPath } from "node:url";
import { startRelay } from "./relay.ts";

const port = Number(process.env.PORT ?? 8787);
const host = process.env.HOST || undefined;
const hostSecret = process.env.RELAY_HOST_SECRET || undefined;
const publicDir = process.env.PUBLIC_DIR ?? fileURLToPath(new URL("../public", import.meta.url));

const relay = await startRelay({ port, host, hostSecret, publicDir });
console.log(`[relay] listening on ${host ?? "*"}:${relay.port}  (host -> /host, players -> /play, console -> /)`);
if (!hostSecret) console.log("[relay] RELAY_HOST_SECRET not set: anyone can register a lobby. Fine for local dev only.");

for (const signal of ["SIGINT", "SIGTERM"] as const) {
  process.on(signal, () => void relay.close().then(() => process.exit(0)));
}
