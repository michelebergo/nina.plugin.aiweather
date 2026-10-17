# AI Weather - All Sky Camera Monitor for NINA

[简体中文](README.zh-CN.md) | English

A plugin for [N.I.N.A.](https://nighttime-imaging.eu/) (Nighttime Imaging 'N' Astronomy) that uses artificial intelligence to analyze images from all-sky cameras and determine real-time weather conditions. It integrates directly with NINA's safety monitoring system to automatically protect your astronomy equipment when conditions become unsafe.

## Why This Plugin?

Unattended imaging sessions are at risk from sudden weather changes. Traditional cloud sensors measure a narrow slice of sky and may miss approaching clouds or fog. An all-sky camera sees the entire sky dome, and AI can interpret that image in ways that go beyond simple brightness thresholds: it distinguishes between thin cirrus and dense overcast, detects rain droplets on the lens, and identifies fog. AI Weather bridges the gap between a camera image and an actionable safety decision, letting NINA pause or abort a sequence before your equipment is damaged.

## Features

### One-package LAN primary/replica mode

The same plugin package supports three explicit roles. **Standalone** analyzes locally. **Primary** runs the configured analysis, writes the teacher-student dataset and publishes its final safety verdict over an authenticated LAN endpoint. **Replica** normally follows that verdict while displaying its own synchronized camera preview; preview and analysis share the terminal's video session. It only runs its own analysis during an explicitly enabled local failover, and does not write the dataset during takeover.

This is deliberately not automatic leader election. A role is frozen when the NINA Safety Monitor connects, so changing it requires disconnecting and reconnecting the device. Replica startup, authentication/protocol errors, stale transport, expired primary decision inputs and an unsafe primary verdict all report **Unsafe**. Explicit OR on the primary may use its external sensor when sky data is stale; see section 7 for compatibility and safety boundaries. Configure a unique shared token of at least 16 characters and restrict the primary TCP port (default `18910`) to trusted observatory hosts with Windows Firewall. See the [frozen cluster design](docs/LAN_PRIMARY_REPLICA_CLUSTER_DESIGN.zh-CN.md) and [latest integration notes](docs/UPSTREAM_MERGE_20261017.zh-CN.md) for the protocol and safety model.

Optional primary-preferred failover lets a Replica take over locally only after a sustained network outage and only when it has a valid encrypted camera/AI configuration. It returns to the Primary after a stable recovery window; authentication and protocol errors never authorize takeover. In Replica mode the synchronized takeover configuration is shown as a read-only panel with its revision and synchronization time. Camera/API secrets are never displayed—only redacted sources and configured/not-configured status. Turning synchronization off switches to explicitly editable local standby values and ignores any older synchronized cache.

### AI-Powered Sky Analysis

The plugin sends captured sky images to a vision-capable AI model that evaluates:

- **Cloud coverage** as a percentage (0-100%)
- **Weather condition** classification (Clear, Partly Cloudy, Mostly Cloudy, Overcast, Rainy, Foggy, or Unknown when the sky cannot be read)
- **Rain detection** including water droplets on the lens, streaks, and condensation
- **Fog detection** based on uniform haze and low contrast
- **Confidence score** indicating how certain the AI is about its assessment
- **Natural language description** explaining what the AI sees in the image

### Multiple Capture Modes

Choose how the plugin acquires sky images based on your camera setup:

| Mode | Best For | How It Works |
|------|----------|--------------|
| **RTSP Stream** | Network IP cameras | Connects to a live RTSP video stream with real-time preview. Uses OpenCV with an automatic LibVLC fallback for maximum camera compatibility. |
| **HTTP Image** | Remote cameras, INDI devices | Periodically downloads a single image from an HTTP/HTTPS URL. Supports Basic authentication. Lower resource usage than continuous streaming. |
| **Folder Watch** | Any camera software | Monitors a local folder for the latest image file (.jpg, .png, .bmp, .tif). Compatible with AllSky, SharpCap, UFOCapture, ASI Studio, and any software that saves images to disk. |


ADVANCED first captures analysis frames from the terminal's existing LibVLC preview.
When an independent decoder is needed, it continuously drains the stream into a bounded
latest-frame slot and requires a newer, recent frame for each check. It is not an idle
FFmpeg session read only once every few minutes. The upstream open/grab/close-per-check
implementation is therefore not substituted for this shared-session design.

Benign decoder chatter goes to Debug, while network failures remain visible. Sustained
late frames still feed ADVANCED's aggregated warning and preview-recovery watchdog.
### Multiple AI Providers

| Provider | Models | Requirements |
|----------|--------|--------------|
| **Local** | Bundled site-trained MobileNetV3 ONNX model | None (works offline on CPU) |
| **GitHub Models** | ⚠️ Retired by GitHub on July 30, 2026 — no longer works; analyses fall back to Local | — |
| **OpenAI** | Listed vision models or a custom vision-model ID | API key |
| **Google Gemini** | One operator-selected model; no automatic switching, retry, rotation, downgrade or backoff | Billed-project API key |
| **Google Gemini Free** | Reorderable Flash/Lite model pool with per-model quota circuits and configurable full-pool cycles | Free-tier API key |
| **Anthropic Claude** | Claude models returned by the API; default Haiku 5.5 | API key |
| **Ollama / Custom** | Any vision model served by Ollama, LM Studio, llama.cpp or LocalAI (e.g. LLaVA, Qwen2.5-VL) | Local server URL (no API key) |

If a cloud AI provider fails or times out (60-second limit), the orchestration layer explicitly falls back to local analysis so that safety monitoring is never interrupted. A fallback remains machine-identifiable and can never be recorded as a successful online teacher label.

Gemini is split into two explicit operator policies. **Google Gemini** uses a separate billed-project key and exactly one selected model: one analysis sends one request and never reorders, downgrades, rotates, retries, or opens a cross-check backoff circuit. **Google Gemini Free** hides the single-model selector and instead walks a reorderable model pool; each model has its own quota circuit, and the complete pool is repeated for the configured number of cycles (default two) before online failure is returned. Both choices retain exact per-attempt model/status/duration diagnostics and independent “once every `N` weather checks” pacing. See the [Gemini tier and availability design](docs/GEMINI_AVAILABILITY_FAILOVER.zh-CN.md).

### Safety Monitor Integration

The plugin registers as a NINA Safety Monitor device. When connected:

- It periodically captures and analyzes sky images at a configurable interval (1-60 minutes).
- It reports **Safe** or **Unsafe** to NINA based on cloud coverage threshold, rain, and fog.
- NINA's sequencer can automatically pause or abort imaging when the status changes to Unsafe.
- An optional status file can be written for integration with ASCOM Generic File SafetyMonitor or external automation tools.

The reported state combines three independent conditions — **anything unknown counts
as unsafe** on its own side, because a missing answer is not permission to keep imaging:

| Condition | Unsafe when |
|---|---|
| Sky verdict | cloud coverage above the threshold, rain, or fog (with hysteresis between the high and low thresholds) |
| Data freshness | no successful analysis within the maximum data age — a dead camera or unreachable stream can never leave a stale **Safe** standing |
| External monitor | the optional ASCOM safety monitor reports unsafe, or cannot be connected or read |

Sky verdict and freshness are always ANDed. The external monitor is combined with **AND**
(the default: either side stops imaging) or **OR** (imaging stops only when both sides say
unsafe; see section 7).

The preview panel shows the **reason** under the Safe/Unsafe line, so a cloudy sky, a
camera that stopped delivering and an unreachable external device are never confused for
one another.

A cloud AI provider failing or timing out does **not** make the state unsafe on its own:
every provider falls back internally to the offline local analyzer, so the same image still
produces a verdict. Only the absence of a *new analysis* ages the state out.

### Live Preview Panel

The preview panel in NINA shows:

- Live video (RTSP) or the latest captured image (HTTP/Folder mode)
- Safety status with color-coded indicator
- Weather condition, cloud coverage percentage, and confidence score
- Rain and fog detection flags
- Full AI description of sky conditions
- Real-time activity log of captures, analyses, and events
- Controls to force an immediate refresh or save the current image

The engineering records for the two independent RTSP failure modes are available in Chinese: [stale analysis frames](docs/RTSP_STALE_FRAME_INCIDENT.zh-CN.md) and [white/blank native live preview airspace](docs/RTSP_PREVIEW_NATIVE_AIRSPACE_INCIDENT.zh-CN.md). A working preview does not prove that the analysis frame is fresh, and a successful analysis does not prove that LibVLC is visibly presenting its HWND; the two paths are tested separately.

If LibVLC enters `Playing` before its first displayable frame is exposed, the preview now keeps the same RTSP player alive and performs bounded delayed-surface probes. A later decoded size or shared AI snapshot immediately uncovers that existing native surface; recovery does not open a second RTSP session.

### Dataset Label Reviewer

The built-in reviewer flattens the sharded teacher-student dataset into a searchable list. It can accept the teacher label, save a separate human correction, reject a sample while retaining its evidence, reset the review state, or permanently delete a useless sample to release disk space. Permanent deletion requires an explicit confirmation and removes the selected label, review sidecar and its image; if another label references the same content-addressed image, that shared image is retained. A compact deletion tombstone records the sample ID, time, number of deleted files and released bytes without retaining the discarded weather data.

## Installation

1. Download the latest advanced release from the [Releases](https://github.com/sedirk/nina.plugin.aiweather-advanced/releases) page.
2. Extract the plugin files to your NINA plugins folder:
   ```
   %LOCALAPPDATA%\NINA\Plugins\
   ```
3. Restart NINA.
4. Go to **Options > Plugins** to configure AI Weather.

## Configuration

### 1. Select a Capture Mode

In the plugin options, choose the capture mode that matches your camera:

- **RTSP Stream**: Enter the stream URL (e.g. `rtsp://192.168.1.100:554/stream`) and optional credentials.
- **HTTP Image**: Enter the image URL and optional credentials.
- **Folder Watch**: Browse to the folder where your camera software saves images.

### 2. Choose an AI Provider

- **Local** requires no setup and works offline. It runs the bundled site-trained MobileNetV3 ONNX model on CPU to estimate cloud coverage and classify weather conditions. This first feasibility model is camera/site-specific and is not a substitute for an independent physical rain or humidity sensor.
- **Google Gemini Free** is the best-effort free-tier choice: enter a free-project key and tune the ordered model pool if needed. **Google Gemini** is the strict single-model choice for a billed project. Keys can be created in [Google AI Studio](https://aistudio.google.com/apikey).
- **OpenAI** and **Anthropic** require their respective API keys from each provider's developer portal.
- **GitHub Models** was retired by GitHub on July 30, 2026 and no longer works for anyone; if selected, analyses fall back to the bundled Local ONNX model.

- **Ollama / Custom** runs fully local: point it to your server URL (default `http://localhost:11434/v1`) and pick a vision model (e.g. `llava`, `qwen2.5vl`). Works with Ollama, LM Studio, llama.cpp, and LocalAI — no API key needed. Thinking-capable models (Gemma 4, Qwen 3.x, DeepSeek) reason at length before answering by default, which can multiply analysis times past the timeout: the "Disable model thinking" option (on by default) turns that off. Uncheck it only on fast hardware.

**The model box is editable.** Select a model or type an ID and leave the field to save it. This does not change the separate Gemini Free ordered-pool policy.

Each Gemini choice has its own **online request: once every N weather checks** setting and API key. `1` calls that policy on every eligible weather check. A larger value spends fewer requests without reducing camera capture or local safety freshness. For example, a 2-minute weather interval over an 8-hour night contains about 240 checks, so `N=12` schedules roughly 20 online pool/single-model runs. Only successful online calls can create teacher labels.

### Shared knowledge wiki

While monitoring, the plugin appends a compact daily sky digest (condition and
safety changes only) to the shared NINA LLM wiki at `%LOCALAPPDATA%\NINA\llmwiki\raw\`.
The [AI Assistant](https://github.com/michelebergo/nina.plugin.aiassistant) plugin
reads that wiki to answer questions about your site, and the
[nina.autopilot](https://github.com/michelebergo/nina.autopilot) ingest agent
consolidates the digests into observing-site pages (fog patterns, typical
seeing, seasonal transparency). Purely append-only and local — no data leaves
your machine.

### 3. Tell it about your camera and your sky

Two things the analysis cannot guess from a single frame, and used to assume:

- **Camera view** — *All-sky fisheye* (the default) tells the AI the sky is a
  circle and the corners are housing. *Fixed camera* is for a security camera or
  any lens that looks at part of the sky above a horizon: the sky is the region
  above the treeline or the buildings, and coverage is judged on that region
  alone.
- **Site darkness** — *Light-polluted* (the default) is the classic picture:
  clouds lit from below look bright and milky, a dark sky with stars is clear.
  At a *Dark site* (roughly Bortle 1-4 with no moon) nothing lights the clouds:
  they are black holes in the star field, and a fully overcast sky is simply
  black. With this setting the AI judges by where the stars are missing in an
  otherwise readable sky rather than by brightness alone. A frame with no
  readable sky information follows the Unknown rule below.

Whatever the settings, a frame whose sky carries no information at all — no
stars, no cloud structure, no moon, no gradient — is reported as **Unknown**
and treated as Unsafe rather than guessed. Isolated specks spread evenly over
the whole frame, including over trees and ground, are read as sensor noise,
not stars.

The defaults retain the all-sky, light-polluted preset. The new noise and
unreadable-sky rules apply to every preset, so results can differ from earlier
versions. These options affect prompt-based providers, not the bundled ONNX
student's inference. Replica takeover uses the encrypted, synchronized camera
and site settings; upgrade both peers before enabling non-default presets or
site notes. See the [integration notes](docs/UPSTREAM_MERGE_20261017.zh-CN.md).

### 4. Optional: Site notes

Every site has visual quirks a general-purpose prompt cannot anticipate. The
optional **Site notes** field lets you describe them in your own words, and the
text is appended to the analysis prompt for every provider:

> Clouds toward the south are lit orange by the city and can look like overcast.
> In daylight the sun reflects off the dome and creates a hazy halo.

The notes steer interpretation only — the classification thresholds, the safety
rules and the response format stay with the built-in prompt, so a note can
explain what a feature is without redefining what "safe" means. Keep it to a few
sentences: the text is sent with every analysis, and long prompts degrade small
local models. Leave it empty and nothing changes.

### 5. Set Monitoring Parameters

- **Check Interval** (minutes): How often the plugin captures and analyzes an image. 5-10 minutes is recommended for active monitoring.
- **Gemini / Gemini Free online request ratio**: each policy has independent pacing; local safety analysis still runs on each intervening check.
- **Cloud Coverage High Threshold** (%): while the status is **Safe**, cloud coverage at or above this value turns it **Unsafe**. Default is 70%.
- **Cloud Coverage Low Threshold** (%): while the status is **Unsafe**, cloud coverage must drop below this value before it returns to **Safe**. Default is 60%.

These are the two edges of a hysteresis band. Between them the status does not
change: with 70 / 60, a reading of 65% preserves the previous state. This stops
a sky sitting near one threshold from repeatedly flipping a sequence between
Safe and Unsafe.

The Low threshold controls when imaging may resume. If it is set far below the
site's normal clear-sky reading, the state can turn Unsafe and never recover.
Keep Low below High; equal values remove the hysteresis, while an excessively
wide band can leave the state stuck. A difference of 5 to 15 percentage points
is a useful starting range.

### 6. Fail-safe

- **Maximum data age** (minutes, 0 = automatic): if no analysis succeeds within this time,
  the sky input becomes **Unsafe** instead of holding the last known state. Automatic means
  three check intervals, never below 10 minutes. With the default AND policy, or without an
  external monitor, this stops imaging. Explicit OR mode can instead rely on a readable,
  safe external device; it never refreshes the timestamp of an old sky analysis.

### 7. Optional: External ASCOM Safety Monitor

- Enable **Combine with an external safety monitor** and pick a device with **Choose...**
  (or type its ProgID), then choose how the two verdicts combine:
  - **AND** (the default): imaging is **Safe** only when the sky analysis *and* the
    external device both say so. The pairing for unattended operation: this plugin watches
    the sky (cloud/rain/fog) while the device watches the environment (humidity, dew point,
    a rain sensor). NINA keeps a single Safety Monitor selected — this one — and gets both
    protections through it.
  - **OR**: imaging stops only when *both* say **Unsafe**. For a second cloud sensor that
    cries cloudy too often — a narrow-field IR sensor reacting to clouds far from the
    target while AI Weather judges a crop of the sky around the pointing (Folder Watch):
    the device confirms the sky verdict instead of adding to it.
- A device that cannot be connected or read counts as **Unsafe**, and sky data older than
  the maximum data age counts as **Unsafe**. With AND that stops imaging — a driver that
  stops answering is exactly the situation its owner installed it for. With OR the other
  side decides alone, and the panel's reason line says which one carried the verdict
  ("Safe on the sky analysis alone — external monitor unsafe or unreadable").
- Fresh rain, fog or an explicit unreadable-sky observation is **Unsafe** in both modes.
  As in upstream, an expired observation no longer vetoes a fresh external input in
  explicit OR mode. OR is not appropriate when either sensor must independently stop imaging.
- Disconnection, stopped monitoring and the daylight guard remain **Unsafe** in both modes.
  A following LAN replica requires a fresh, authenticated, safe primary verdict and may only
  add a local external-device veto; its OR setting cannot override primary failure or Unsafe.
  Local takeover requires a first valid local analysis before local OR can apply. External
  device configuration and AND/OR selection remain local to each computer, not synchronized.
- LAN snapshots distinguish final decision freshness (`safetyDecisionFresh`) from sky
  freshness (`sourceFresh`) and preserve the real analysis timestamp. New replicas can follow
  an external-only OR decision; old replicas conservatively reject it when sky data is stale.
  Upgrade both ends to use the new behavior.

### 8. Optional: Status File

- Enable **Write Safety Status File** to output the current status to a text file, useful for external scripts or the ASCOM Generic File SafetyMonitor driver. The file carries the same combined state NINA acts on.

## Usage

### Connecting the Safety Monitor

1. In NINA, go to **Equipment > Safety Monitor**.
2. Select **All Sky Camera Safety Monitor**.
3. Click **Connect**.

The plugin will begin periodic monitoring. The safety status is reported to NINA and any running sequence will respond according to its safety instructions.

### Using the Preview Panel

Navigate to the AI Weather preview panel to see:

- The current sky image or live video stream
- Analysis results updated after each check cycle
- An activity log showing connection events, captures, and AI responses

Use the **Refresh** button to trigger an immediate capture and analysis outside the regular interval.

### Weather Conditions Reference

| Condition | Cloud Coverage | Rain/Fog | Safe? |
|-----------|---------------|----------|-------|
| Clear | < 15% | No | Yes |
| Partly Cloudy | 15-50% | No | Yes |
| Mostly Cloudy | 50-85% | No | Depends on threshold |
| Overcast | > 85% | No | No |
| Rainy | Any | Rain detected | No |
| Foggy | Any | Fog detected | No |
| Unknown | reported as 100% | Sky cannot be read | No |

Rain and fog always trigger an Unsafe status regardless of the cloud coverage threshold. So does a frame the AI cannot read: a black or featureless sky is never taken as evidence of clear weather.

### Sequencer Symbols (N.I.N.A. 3.3+)

On N.I.N.A. 3.3 and later, every analysis publishes its values to the Advanced Sequencer's **Symbols** sidebar under an **AIWeather** category, so they can be used in expressions and message templates (for example in Discord/notification instructions):

| Symbol | Type | Meaning |
|--------|------|---------|
| `Cloud` | number | Cloud coverage of the last analysis, 0-100 % |
| `Confidence` | number | The AI's confidence in that analysis, 0-100 % |
| `Condition` | text | `Clear`, `PartlyCloudy`, `MostlyCloudy`, `Overcast`, `Rainy`, `Foggy`, `Unknown` |
| `Rain` | boolean | Rain detected in the last image |
| `Fog` | boolean | Fog detected in the last image |
| `Safe` | boolean | The monitor's current verdict (including hysteresis) |

Reference them directly (`Cloud`, `Confidence`), or as `AIWeather_Cloud` if another plugin ever defines a symbol with the same name. The values refresh with every monitoring cycle while the safety monitor is connected, and are blanked when it disconnects so an expression can never act on a stale reading. On N.I.N.A. versions before 3.3 the plugin behaves exactly as before — no symbols, no errors.

## Building from Source

**Requirements:**
- Visual Studio 2022 or later
- .NET 8.0 SDK
- NINA 3.x installed (for assembly references)

```
git clone https://github.com/michelebergo/nina.plugin.aiweather.git
cd nina.plugin.aiweather
dotnet restore
dotnet build
```

## Support the project

This plugin is developed and maintained independently in my free time.

If it is useful for your astrophotography or automation workflow, you can support its continued development:

[☕ Support development on Buy Me a Coffee](https://buymeacoffee.com/michelebergo)

Your support helps cover development tools, testing hardware, hosting and future features.

## License

MIT License. See [LICENSE](LICENSE) for details.

## Contributing

Contributions are welcome. Please fork the repository, create a feature branch, and submit a pull request.

## Support

- **Issues**: [GitHub Issues](https://github.com/michelebergo/nina.plugin.aiweather/issues)
- **NINA Community**: [NINA Discord](https://discord.gg/nighttime-imaging)

### ADVANCED 1.35.0.0: upstream 1.15.7 integration (2026-10-09)

Merged all five upstream commits with ADVANCED-specific adaptations: NINA-themed editors,
camera credential preservation, cancellation handling, invalid-result rejection and
configuration-aware 30-second deduplication of immediate checks within a connection.
Reconnect still starts Unsafe and requires a fresh result; deduplication never renews the
analysis timestamp. The existing shared RTSP/latest-frame pipeline and late-frame recovery
watchdog are retained. See the [merge decisions and release notes](docs/RELEASE_1.35.0.0.zh-CN.md).

### ADVANCED 1.34.0.0: upstream 1.15.5 integration (2026-09-16)

The Gemini Free default order now inserts `gemini-3.8-flash` before `gemini-3.7-flash`, after the two Flash-Lite models. Existing customized relative ordering is preserved. Each model retains an independent quota circuit: a daily quota rejection on one model does not stop traversal of the others. Pool failures now report each model's outcome, and only advertise a pool-wide retry time when every entry is quota-paused.

Upstream response parsing, Gemini request profiles, truncated-answer detection, provider testing, provider health diagnostics and frame identity logging have been adapted to ADVANCED's provenance and ONNX fallback. Preview lifecycle fixes use the existing shared RTSP session: off-screen starts are deferred, and unexpected playback loss reconnects with a bounded pause. Paid Gemini remains one request to the selected model per check. See [integration notes](docs/UPSTREAM_MERGE_20260916.zh-CN.md) for scope and validation.

The dataset label inspector now sorts teacher/student cloud coverage and their differences numerically rather than as formatted percentage strings. See the [1.34.0.0 release notes](docs/RELEASE_1.34.0.0.zh-CN.md) for the complete release scope.
