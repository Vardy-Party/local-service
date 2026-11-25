# MP segment rewrite reverse-engineering

## Status: full URL assemble from playlist — **SOLVED offline**

Given playlist-relative segments (with `_ctump` / `_ctuph` / `_ctutt`), the rewritten KDNS URL is recoverable without Chrome.

### Pipeline
1. **Decode** each `_ctu*` value: `atob(rot13(raw.slice(8)))` (after URI-decode).
2. **`_ctump`** → `WW:cf@{host}` (e.g. `example.invalid`).
3. **`_ctuph`** → `/cfall/…/super/{STEM}.{ext}?_ver={V}&_s1={S1b}` (host, decoy ext, rewritten `_s1` already baked in).
4. **`_ctutt`** → timeout (e.g. `2.5`).
5. **`_s2`** = `md5(\`${file},${_ver},00\`)` (SW `Wt`; salt default `00`).

```
rewritten = https://{host from ctump}{path+query from ctuph}&_s2={md5}
```

### Proofs
| Check | Result | Script |
| --- | --- | --- |
| `_s2` md5 | **309/309** KDNS URLs | `deobf/prove-s2-md5.mjs` |
| Full assemble vs observed | **23/23** stem-paired playlist↔rewritten | `deobf/decode-ctu.mjs` |

Docs: `deobf/DEOBFUSCATION.md`.

### Who does what
| Component | Role |
| --- | --- |
| Playlist origin | Encrypts host + path/`_s1`/ext into `_ctu*` |
| `nadia-sw.js` | Decrypts CTU, appends `_s2`, fetches (or HlsProxy) |
| `2178.js` | HlsProxy P2P bridge only — **no mint** |
| **`VardyParty.LocalService.V2`** (Strategies NuGet) | Offline CTU rewrite for Client + LocalService |
| **`VardyParty.LocalService.V2.Chrome`** | LocalService Chrome host for `POST /mp` (not a NuGet strategy) |

Client playback: LocalService resolves playlist URL (+ headers); Client applies `MpPlaylistRewriter` (V2 package) so native players fetch absolute KDNS segment URLs. Strategy comes from catalog `resolutionStrategy` / `source`, not URL hosts.

### Still not solved (optional next)
- How the **CDN/origin** chooses host ring, decoy ext, and `S1b` when minting `_ctu*` (needed only if you lack a playlist).
- Non-default `salt`; multi-host CTU failover selection.

### Earlier notes
- Same stem ⇒ identical rewritten URL across tokens (deterministic from `_ctu*` + `_s2` formula).
- Assembled URL TTL: immediate GET OK, ~60s → 442 without live session cookies/edge state as before.
