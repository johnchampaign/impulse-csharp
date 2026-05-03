# Setting up the log collection backend

The "SUBMIT LOGS" button in the app POSTs game logs to a Cloudflare Worker
which commits each log as a content-addressable file in a public GitHub
repo (`impulse-game-logs`). This document walks through deploying that
Worker.

**Estimated time:** 10–15 minutes. **Cost:** $0 (Cloudflare Workers free
tier handles 100k requests/day; GitHub repos are free for public content).

You only need to do this **once**, as the app developer. Players never
need to do anything to submit logs beyond clicking the button.

---

## Step 1 — Create the public log-collection repo

1. Go to https://github.com/new
2. Owner: `johnchampaign` (or your account)
3. Name: `impulse-game-logs`
4. Public ✅ (logs are open data; required for downstream use)
5. Add a README ✅
6. Click **Create repository**

This is where every submitted log will land as a file at `logs/<hash>.log`.

## Step 2 — Generate a fine-grained GitHub Personal Access Token

1. Go to https://github.com/settings/personal-access-tokens/new
2. Token name: `impulse-log-collector`
3. Expiration: 1 year (rotate annually)
4. Repository access: **Only select repositories** → choose `impulse-game-logs`
5. Repository permissions:
   - **Contents** → **Read and write**
   - **Metadata** → **Read-only** (auto-selected)
6. Generate token, **copy it immediately** (you won't see it again).

## Step 3 — Deploy the Cloudflare Worker

Prerequisites: a Cloudflare account (free, sign up at https://dash.cloudflare.com/sign-up).

```bash
cd worker
npm install
npx wrangler login                     # opens browser, authenticates
npx wrangler secret put GITHUB_TOKEN   # paste the PAT from step 2
npx wrangler deploy
```

After `wrangler deploy`, the CLI prints a URL like:
```
https://impulse-logs.<your-subdomain>.workers.dev
```

## Step 4 — Wire the Worker URL into the WPF app

Open `src/Impulse.Wpf/MainWindow.xaml.cs` and update the constant:

```csharp
private const string LogSubmitEndpoint =
    "https://impulse-logs.<your-subdomain>.workers.dev/submit";
```

Rebuild + ship. New release binaries will start sending logs to your Worker.

## Step 5 — Test the round-trip

1. Run the app, play a game (or just click SUBMIT LOGS with archived logs in `%TEMP%`)
2. Check `https://github.com/<owner>/impulse-game-logs/tree/main/logs` —
   each accepted log appears as `<sha256>.log`
3. To run analysis on collected logs:
   ```
   git clone https://github.com/<owner>/impulse-game-logs
   dotnet run --project src/Impulse.Bench -- --replay-dir impulse-game-logs/logs
   ```

## How dedup works

The Worker computes `sha256(log_content)` and uses that as the filename.
- Same content → same filename → second upload is detected as duplicate
  (HTTP 200 with `{"duplicate": true}`)
- Different content → different filename → committed as a new file

So clicking SUBMIT LOGS multiple times never causes duplicates. The app
sends every log on every click; the Worker filters to net-new content.

## Anti-abuse

The Worker validates each submission:
- POST only, to `/submit` only
- Body must be JSON `{ log: string, version?, os? }`
- Log size: 200 bytes < log < 5 MB
- Log must start with `# impulse log opened` (the canonical header)

For higher volume protection, enable Cloudflare's free WAF rate-limit:
Cloudflare dashboard → your domain → Security → WAF → Rate limiting rules
→ "100 requests per IP per 15 min on /submit". Free plan supports this.

## Costs at scale

- Cloudflare Workers free tier: 100,000 requests/day (way more than enough)
- GitHub: free for public repos with reasonable file count (logs are
  ~50–100 KB each; 10,000 logs = ~500 MB, well within limits)

If volume ever exceeds free tier, switch the Worker storage from
"GitHub commit" to "Cloudflare R2 bucket" — also free up to 10 GB.

## Worker source

See [`worker/src/index.ts`](worker/src/index.ts). The whole thing is
~120 lines of TypeScript with no external dependencies beyond
`@cloudflare/workers-types`.
