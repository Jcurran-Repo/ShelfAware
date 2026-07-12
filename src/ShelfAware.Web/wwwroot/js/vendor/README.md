# Vendored audio worklets (cook-along voice)

The ElevenLabs Agents SDK (`@elevenlabs/client`, pinned `1.14.0` on esm.sh) loads three AudioWorklet
modules at session start. Its defaults — a jsdelivr CDN URL for the resampler, and `blob:`/`data:`
fallbacks for its own processors — are all blocked by this app's strict production CSP
(`script-src 'self' https://esm.sh`). The SDK supports self-hosting via the `workletPaths` +
`libsampleratePath` session options (its own error text recommends exactly that under a strict CSP),
so these files are served from our origin instead. **No CSP directive was loosened for this.**

| File | Origin | Version |
|---|---|---|
| `libsamplerate.worklet.js` | `https://cdn.jsdelivr.net/npm/@alexanderolsen/libsamplerate-js@2.1.2/dist/libsamplerate.worklet.js` (verbatim; MIT) | 2.1.2 |
| `raw-audio-processor.worklet.js` | inline worklet source extracted from `@elevenlabs/client@1.14.0` `dist/platform/web/rawAudioProcessor.generated` (MIT) | 1.14.0 |
| `audio-concat-processor.worklet.js` | inline worklet source extracted from `@elevenlabs/client@1.14.0` `dist/platform/web/output` (MIT) | 1.14.0 |

These are third-party files under their own licenses, NOT under the repo's PolyForm Noncommercial
license. The full copyright + permission notices (MIT ×3, plus the compiled `libsamplerate` C
library's 2-clause BSD) live in [`THIRD-PARTY-NOTICES.md`](../../../../../THIRD-PARTY-NOTICES.md)
at the repo root — keep that file in sync if a vendored file is added, removed, or re-extracted.

Note: `libsamplerate.worklet.js` is the package's **wasm2js** build — the resampler is compiled to plain
JavaScript, so it needs no `wasm-unsafe-eval` allowance.

**If the SDK pin in `js/cookalong.js` is ever bumped, re-extract the two SDK worklets from the new
version** (the processor sources live inline in the modules above) and re-check the libsamplerate
version the SDK's `addLibsamplerateModule` default points at.

**Known SDK bug (1.14.0) + shim:** `startSession` threads `libsampleratePath` into `Input.create` but
NOT `Output.create`, so the output resampler ignores the override and requests the jsdelivr URL anyway.
`cookalong.js` therefore installs a small `AudioWorklet.prototype.addModule` shim that rewrites exactly
that one pinned URL to the vendored copy. When a fixed SDK version ships (Output.create receiving
`libsampleratePath`), bump the pin and delete the shim.
