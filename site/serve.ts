// Zero-dependency static file server for local preview of the NightOwl website.
// Run with: node site/serve.ts
import { createServer } from "node:http";
import { readFile } from "node:fs/promises";
import { extname, join, normalize } from "node:path";

const root: string = join(import.meta.dirname, ".");
const port = 4173;
const base = "/nightowl/";

const types: Record<string, string> = {
  ".html": "text/html; charset=utf-8",
  ".css": "text/css; charset=utf-8",
  ".svg": "image/svg+xml",
  ".ts": "text/plain; charset=utf-8",
};

createServer(async (req, res) => {
  const url = req.url ?? "/";
  if (url === "/") {
    res.writeHead(302, { Location: base });
    return res.end();
  }

  let relative = url.startsWith(base) ? url.slice(base.length) : url.slice(1);
  if (relative === "" || relative.endsWith("/")) relative += "index.html";
  const filePath = normalize(join(root, relative));

  if (!filePath.startsWith(root)) {
    res.writeHead(403);
    return res.end("Forbidden");
  }

  try {
    const data = await readFile(filePath);
    res.writeHead(200, { "Content-Type": types[extname(filePath)] ?? "application/octet-stream" });
    res.end(data);
  } catch {
    res.writeHead(404);
    res.end("Not found");
  }
}).listen(port, () => {
  console.log(`NightOwl site: http://localhost:${port}${base}`);
});
