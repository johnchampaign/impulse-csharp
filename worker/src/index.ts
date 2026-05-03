// Cloudflare Worker: receives game-log POSTs from the Impulse WPF app and
// commits each one to a public GitHub repo (`impulse-game-logs`) for use
// as a public AI-training dataset.
//
// Filenames are content-addressable (sha256 of the log bytes), so:
//   • Re-submitting the same log is a no-op (filename collision detected).
//   • Anyone can verify a log file is unchanged by hashing it themselves.
//
// Deploy:
//   wrangler deploy
//   wrangler secret put GITHUB_TOKEN     # paste your fine-grained PAT
//
// Required secrets / vars (see wrangler.toml + secrets):
//   GITHUB_TOKEN  — fine-grained PAT with contents:write scoped to GH_REPO
//   GH_OWNER      — e.g. "johnchampaign"
//   GH_REPO       — e.g. "impulse-game-logs"
//   GH_BRANCH     — e.g. "main"

export interface Env {
  GITHUB_TOKEN: string;
  GH_OWNER: string;
  GH_REPO: string;
  GH_BRANCH: string;
}

interface SubmitBody {
  log?: string;
  version?: string;
  os?: string;
}

const MIN_LOG_BYTES = 200;
const MAX_LOG_BYTES = 5_000_000;
const REQUIRED_HEADER_PREFIX = "# impulse log opened";

export default {
  async fetch(req: Request, env: Env): Promise<Response> {
    if (req.method !== "POST") {
      return json({ error: "method not allowed" }, 405);
    }
    const url = new URL(req.url);
    if (url.pathname !== "/submit") {
      return json({ error: "not found" }, 404);
    }

    let body: SubmitBody;
    try {
      body = (await req.json()) as SubmitBody;
    } catch {
      return json({ error: "invalid json" }, 400);
    }
    const log = (body.log ?? "").trim();
    if (log.length < MIN_LOG_BYTES) {
      return json({ error: "log too small or empty" }, 400);
    }
    if (log.length > MAX_LOG_BYTES) {
      return json({ error: "log too large" }, 413);
    }
    if (!log.startsWith(REQUIRED_HEADER_PREFIX)) {
      return json({ error: "log missing expected header" }, 400);
    }

    const hash = await sha256Hex(log);
    const filename = `logs/${hash}.log`;

    // Check if file already exists in the repo (dedup).
    const head = await fetch(
      `https://api.github.com/repos/${env.GH_OWNER}/${env.GH_REPO}/contents/${filename}?ref=${env.GH_BRANCH}`,
      { headers: ghHeaders(env) },
    );
    if (head.status === 200) {
      return json({ ok: true, duplicate: true, sha: hash });
    }

    // Construct commit metadata. Useful comment lines we DO want preserved
    // in the committed file: app version + submission timestamp. We put
    // them in the COMMIT MESSAGE rather than mutating the log content,
    // because changing log bytes would change the hash.
    const commitMessage =
      `Add log ${hash.slice(0, 12)} ` +
      `(version=${(body.version ?? "?").slice(0, 32)}, ` +
      `os=${(body.os ?? "?").slice(0, 32)})`;

    const put = await fetch(
      `https://api.github.com/repos/${env.GH_OWNER}/${env.GH_REPO}/contents/${filename}`,
      {
        method: "PUT",
        headers: ghHeaders(env),
        body: JSON.stringify({
          message: commitMessage,
          content: btoaUtf8(log),
          branch: env.GH_BRANCH,
        }),
      },
    );
    if (!put.ok) {
      const text = await put.text();
      // 422 with "sha" message means it raced and a duplicate landed
      // first — treat as duplicate, not failure.
      if (put.status === 422 && /sha/i.test(text)) {
        return json({ ok: true, duplicate: true, sha: hash });
      }
      return json({ error: `github upload failed: ${put.status}`, detail: text }, 502);
    }
    return json({ ok: true, duplicate: false, sha: hash });
  },
};

function ghHeaders(env: Env): HeadersInit {
  return {
    "Accept": "application/vnd.github+json",
    "Authorization": `Bearer ${env.GITHUB_TOKEN}`,
    "User-Agent": "impulse-log-collector",
    "X-GitHub-Api-Version": "2022-11-28",
  };
}

function json(obj: unknown, status = 200): Response {
  return new Response(JSON.stringify(obj), {
    status,
    headers: { "content-type": "application/json" },
  });
}

async function sha256Hex(s: string): Promise<string> {
  const buf = new TextEncoder().encode(s);
  const digest = await crypto.subtle.digest("SHA-256", buf);
  return Array.from(new Uint8Array(digest))
    .map((b) => b.toString(16).padStart(2, "0"))
    .join("");
}

// btoa() requires Latin-1; logs are UTF-8. Encode UTF-8 → bytes → base64.
function btoaUtf8(s: string): string {
  const bytes = new TextEncoder().encode(s);
  let bin = "";
  for (let i = 0; i < bytes.byteLength; i++) bin += String.fromCharCode(bytes[i]);
  return btoa(bin);
}
