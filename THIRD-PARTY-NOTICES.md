# Third-party notices

Shelf Aware's own code is licensed under the [PolyForm Noncommercial License 1.0.0](LICENSE).
That license covers **first-party code only**. The third-party components below are redistributed
with this repository and remain under **their own licenses**, reproduced here as those licenses
require. Nothing in Shelf Aware's LICENSE relicenses or restricts them.

The NuGet package dependencies (see the `.csproj` files) are **not** redistributed in this
repository — they are fetched from nuget.org at build time and remain under their own licenses
(all MIT or Apache-2.0). The `@elevenlabs/client` SDK itself is loaded at runtime from esm.sh at a
pinned version and is likewise not redistributed here; the extracted worklet files below are.

---

## 1. `@alexanderolsen/libsamplerate-js` 2.1.2

**File:** `src/ShelfAware.Web/wwwroot/js/vendor/libsamplerate.worklet.js` (verbatim copy of the
package's wasm2js worklet build, which also contains the compiled `libsamplerate` C library — both
notices below apply).

### libsamplerate-js License (MIT)

The MIT License (MIT)

Copyright (c) 2021 Alexander Olsen

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated documentation files (the "Software"), to deal in the Software without restriction, including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

### libsamplerate (aka Secret Rabit Code) License (2-clause BSD)

Copyright (c) 2012-2016, Erik de Castro Lopo erikd@mega-nerd.com All rights reserved.

Redistribution and use in source and binary forms, with or without modification, are permitted provided that the following conditions are met:

Redistributions of source code must retain the above copyright notice, this list of conditions and the following disclaimer.

Redistributions in binary form must reproduce the above copyright notice, this list of conditions and the following disclaimer in the documentation and/or other materials provided with the distribution.

THIS SOFTWARE IS PROVIDED BY THE COPYRIGHT HOLDERS AND CONTRIBUTORS "AS IS" AND ANY EXPRESS OR IMPLIED WARRANTIES, INCLUDING, BUT NOT LIMITED TO, THE IMPLIED WARRANTIES OF MERCHANTABILITY AND FITNESS FOR A PARTICULAR PURPOSE ARE DISCLAIMED. IN NO EVENT SHALL THE COPYRIGHT HOLDER OR CONTRIBUTORS BE LIABLE FOR ANY DIRECT, INDIRECT, INCIDENTAL, SPECIAL, EXEMPLARY, OR CONSEQUENTIAL DAMAGES (INCLUDING, BUT NOT LIMITED TO, PROCUREMENT OF SUBSTITUTE GOODS OR SERVICES; LOSS OF USE, DATA, OR PROFITS; OR BUSINESS INTERRUPTION) HOWEVER CAUSED AND ON ANY THEORY OF LIABILITY, WHETHER IN CONTRACT, STRICT LIABILITY, OR TORT (INCLUDING NEGLIGENCE OR OTHERWISE) ARISING IN ANY WAY OUT OF THE USE OF THIS SOFTWARE, EVEN IF ADVISED OF THE POSSIBILITY OF SUCH DAMAGE.

---

## 2. `@elevenlabs/client` 1.14.0

**Files:** `src/ShelfAware.Web/wwwroot/js/vendor/raw-audio-processor.worklet.js` and
`src/ShelfAware.Web/wwwroot/js/vendor/audio-concat-processor.worklet.js` (worklet sources extracted
from the package's `dist/platform/web` modules).

MIT License

Copyright (c) 2025 ElevenLabs

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

---

## 3. `wavefile` (µ-law codec logic)

**Where:** the µ-law encode/decode logic inside the extracted `@elevenlabs/client` worklets above
derives from the `wavefile` library by Rafael da Silva Rocha (https://github.com/rochars/wavefile).

Copyright (c) 2017-2019 Rafael da Silva Rocha.

Permission is hereby granted, free of charge, to any person obtaining
a copy of this software and associated documentation files (the
"Software"), to deal in the Software without restriction, including
without limitation the rights to use, copy, modify, merge, publish,
distribute, sublicense, and/or sell copies of the Software, and to
permit persons to whom the Software is furnished to do so, subject to
the following conditions:

The above copyright notice and this permission notice shall be
included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND
NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE
LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION
OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION
WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.

---

## 4. NuGet dependencies

Fetched from nuget.org at build time, not redistributed in this repository. Versions are the ones the
`.csproj` files pin as of 2026-10-07; the files are the authority if this list drifts.

| Package | Version | License |
|---|---|---|
| `Anthropic` | 12.29.0 | MIT |
| `HotChocolate.AspNetCore` | 16.6.1 | MIT |
| `MailKit` | 4.17.0 | MIT |
| `Microsoft.Extensions.AI.OpenAI` | 10.7.0 | MIT |
| `Microsoft.*` (ASP.NET Core Identity, EF Core SQLite, Google auth, Extensions) | 10.0.9 | MIT |
| `org.k2fsa.sherpa.onnx` | 1.13.8 | Apache-2.0 |
| `SQLitePCLRaw.bundle_e_sqlite3` | 3.0.3 | Apache-2.0 |
| `Stripe.net` | 52.4.1 | Apache-2.0 |

The test projects additionally pull xUnit (Apache-2.0), bUnit (MIT), `Microsoft.CodeAnalysis.CSharp`
(MIT) and `coverlet.collector` (MIT).

### A note on sherpa-onnx's native runtime

The `org.k2fsa.sherpa.onnx` package ships a native library that bundles **espeak-ng** phonemizer
data, which is licensed **GPL-3.0-or-later**. Shelf Aware uses it unmodified, as a runtime component
of a hosted service — the app links against the package's binaries at run time and makes no changes
to them — and does not redistribute it as part of this repository's source. The GPL's obligations
attach to *distribution*: anyone who redistributes a `dotnet publish` output (which contains those
binaries) must honour espeak-ng's licence for that distribution, including its source-availability
terms. Running the published app on your own box, as the deploy docs describe, is not distribution.

---

## 5. Speech models

None of these is in the repository. The deploy docs fetch each one from its upstream release into
`/var/lib/shelfaware-models` (or `app-data/models` on the family box) at deploy time, and the operator
chooses which to run with `Speech:Provider`.

| Model | Fetched by | License |
|---|---|---|
| **Kokoro-82M** (`kokoro-int8-en-v0_19`) | `docs/deploy-kokoro.md` | Apache-2.0 (as that doc records, from the model's own card) |
| **Moonshine** | `docs/deploy-moonshine.md` | MIT (as that doc records) |
| **Piper** voice `en_US-ryan-medium` (and the other `vits-piper-*` voices in the bake-off) | `docs/deploy-piper.md`, `docs/voice-bakeoff.md` | **Per voice.** Each archive carries a `MODEL_CARD` file naming the dataset licence for that voice; the operator must read it before changing voices. |
| **Kitten** (`kitten-nano-*`, `kitten-mini-*`) and **Matcha** (`matcha-icefall-en_US-ljspeech` + its vocoder) | `docs/voice-bakeoff.md` | **Per model.** Same rule: the `MODEL_CARD` in each archive is the authority, and the vocoder Matcha needs is a separate release with its own. |

The per-voice entries are deliberately not summarised here: a Piper or Kitten voice's licence depends
on the dataset it was trained from, and writing one down for a voice someone later swaps out would be
a claim this file could not keep true.

---

## 6. Media

`src/ShelfAware.Web/wwwroot/media/` holds `jingle.mp3`, `song.mp3`, `lyric-video.mp4` and
`reginald-dance.mp4`. These were generated with [Suno](https://suno.com) for this project; the rights
to use them are those Suno's terms grant to the generating account. They are not covered by the
PolyForm licence on the code, and they are not offered for reuse outside this project.
