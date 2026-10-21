# BeatSync Studio Pro — Changelog

Every update since the first working build: what was added, improved, and fixed.
Created by Michael DeCato.

## Version 3.23 — 🏠 Remote GPU: Your Other PC Becomes a Provider
### New
- **🏠 Remote GPU — FREE (my other PC)** (Michael: "is there a way we can make the DJ app use a gpu that's in another computer for the AI lab? …adding that option… if we could do it"). The AI Lab / Create Song now treat **your own second computer as a generation provider**, right next to Replicate, Hugging Face and fal.ai: set Compute to ☁️ Online, pick **🏠 Remote GPU**, and song generation jobs are sent over your home network (or Tailscale, from anywhere) to the **BeatSync Engine** running on that PC — its graphics card (the GTX 1660 desktop or the RTX 3050 laptop) does the work with ACE-Step. **No per-run fees, no cloud, your lyrics and music never leave your own machines** — and the engine is where your 👑 Rad-Dad voice conversion will live once your voice model is trained there.
- The **🌐 Providers** window has a **🏠 Remote GPU card** (🎵 Music list, badged FREE) with an **engine address box** (e.g. http://192.168.1.50:7860), a 💾 Save and a **🩺 Test** button that asks the engine PC to say hello and reports its GPU and model list. The AI Lab's Keys & Compute tab understands the new provider too (no key — it uses the address from Providers).
- The engine speaks a small open contract this client is built against — `GET /health` (GPU + models) and `POST /generate` (title, style prompt, lyrics, duration → audio) — so the engine software itself can be swapped or upgraded on the other PC without touching this app. **Honest status:** the app side is complete and shipped; the engine still has to be installed on the other PC once (that's the next setup step, engine PC pick + one install), and until then Test will plainly say it can't reach it.

## Version 3.22 — Main Window Glow-Up
### Changed
- **The main window pops now** (Michael: "the splash makes u get excited, then open the app and it looks like that… make it pop more like a DJ setup"). Pure visual pass, no behavior changes: the window backdrop is a deeper diagonal gradient with a violet bloom; the brand bar sits over a green→gold→violet neon rule with a glowing wordmark and a gold version chip; panels use vertical gradient fills; the **video preview is framed in a violet-to-green neon border with a soft glow**; every major button is now a gradient — Play glows green, Pause is blue glass, Stop is red glass, the **🎧 DJ button is violet with a glow**, utilities are dark glass — and hovering any button adds a soft light halo; the time readout glows to match.

## Version 3.21 — Compute Choice Now Drives ffmpeg Too
### Changed
- **The CPU/GPU compute choice now covers video export** (Michael: "for the option to use cpu or gpu prolly need to include ffmpeg or things like that too?"). ffmpeg used to encode every export with libx264 on the CPU no matter what was picked. Now the encoder follows the same setting that drives the stem splitter: **CPU → libx264; GPU / Auto → the hardware encoder** for the detected graphics — NVIDIA NVENC, **Intel QuickSync** (yes, integrated Intel counts here; QuickSync is built for exactly this), or AMD AMF. If the hardware encoder ever fails, the export quietly redoes itself on the CPU encoder instead of failing, and the export window's finish message names the encoder that actually did the work.

## Version 3.20 — Providers Split by Category + "Song" Tab
### Changed
- **Providers are now split by category** (Michael's direction — some models do music, some do video, and the two shouldn't be mixed in one list): the Providers window has **🎵 Music & Audio** and **🎬 Video** tabs, each listing only that category's providers — every one still badged **FREE or PAID**, with key boxes and signup/key links where they apply. Services that do both (**fal.ai, Replicate**) appear in **both** lists; one key covers everything they do. **Freesound** joined the Music list (free sounds; its key stays in the Freesound window).
- The AI Lab's **"✨ Create" tab is renamed "🎤 Song"** so the Lab's categories line up with the Providers split: **Song | Video | Keys & Compute**.

## Version 3.19 — Providers Hub + Faded-Button Fix + More Video Models
### New
- **🌐 Providers window** (Michael's ask): one directory for every AI service, opened from the **AI Lab header** (always visible, every tab) and from **Create Song**. Ten providers, each badged **🎵 Audio / 🎬 Video** and **FREE or PAID**: fal.ai, Replicate and Hugging Face have working key boxes (same encrypted store as the Keys tab) plus **🆕 Sign up** and **🔑 Get API key** links; OpenCode and Ollama are the no-key free routes; OpenRouter, Google Gemini, WaveSpeedAI, Kling AI and Luma are honest directory listings — grab the account now, in-app generation arrives as each client lands.
- **Seven more video models** in the Video tab (endpoints confirmed against fal's docs/SDK): Veo 3 (video with sound), Kling 2.1 Master, Kling 1.6 Standard, Luma Ray 2, Wan Pro, PixVerse V4.5, Vidu Q1 — ten in all.

### Fixed
- **"The buttons are faded out in a lot of areas"** (Michael, with a screenshot of the Video tab): the AI Lab used the default light Windows templates for buttons and tab headers on a dark window, and disabled buttons washed to pale grey. The Lab now carries its own dark button template (hover glows violet, disabled dims evenly at 40% instead of washing out) and dark tab headers with a clear selected state. The video prompt box and model picker got proper dark styling too.

## Version 3.18 — Video Generator in the AI Lab
### New
- **🎬 Video tab in the AI Lab**: describe a clip, pick a model, and an AI films it. Runs on **fal.ai** with the key from the Keys & Compute tab (paid per clip — stated plainly on the tab; there is no free hosted video tier that actually works yet, and the home engine will be the free route once it exists). Models, all confirmed against fal's own docs: **MiniMax Video-01** (strong all-rounder), **LTX-2 19B** (fast & cheaper), **LTX-2.3 22B** (newest LTX, higher quality).
- Finished clips download through the **Downloads window** (progress, pause, cancel), preview right in the tab with play/pause/stop, and **🎬 Add to my project** files the clip in the asset library under Video, ready to drag onto the video track. Clips are kept in the app's Generated folder.

## Version 3.17 — Create Song Label Fix
### Fixed
- The Create Song window's **"Length" label overlapped "Voice"** (spotted in Michael's screenshot of the fixed window): the label was missing its grid column, so it rendered on top of the Voice label. It now sits in its own column beside the duration picker.

## Version 3.16 — Cassette Deck Fixes
### Fixed
- **Switching a deck to the cassette left the vinyl behind**: the record's big black neon halo disc was added to the deck outside the vinyl group, so hiding the vinyl never hid it. The halo now lives inside the vinyl view — cassette mode shows the tape, clean, with nothing of the record left over.
- **The cassette now clearly animates while music plays**: each reel carries an accent marker dot (the bare 3-spoke pattern is symmetrical and read as static — the wagon-wheel effect), the reels spin at *different, physical* speeds (the emptier a reel gets, the faster it turns, exactly like a real deck), and the tape pack visibly drains from the left reel to the right across the song, over a wider range than before. Scrubbing and scratching turn the reels too.

## Version 3.15 — Create Song Crash Fix
### Fixed
- **Create Song crashed the whole app the moment it opened** (found via the PC's crash log): the window's XAML sets each influence slider's value while the window is still loading, which fired the slider handler before the % labels beside the later sliders existed — a null-reference crash inside `InitializeComponent`, present since the window was introduced in v3.11 (the AI Lab's Make-This-Into-A-Song button was simply the first door Michael walked through). Every label is now guarded on its own, and the labels re-sync once the window has fully loaded.
- The AI Lab's song hand-off is now wrapped so that if opening Create Song ever fails for any reason, the AI Lab shows the error in its status line instead of the app closing.

## Version 3.14 — Lyrics → Song, Front and Center
### Changed
- **The AI Lab's song hand-off is now impossible to miss**: a big green **🎤 Make THIS into a Song →** button sits right under the results. It carries your generated lyrics into Create Song **with your style already filled in** — from there the style chips, voice picker, influence sliders and 🥁 Auto-make the beat are all on the one screen, then **Generate Song**. After lyrics generate, the status line points you at the button, and if there's nothing generated yet it tells you so instead of opening an empty panel.

## Version 3.13 — Compare AIs + fal.ai
### Added
- **🔀 Compare all AIs** (AI Lab → ✨ Create): check the box, hit Generate, and the **same prompt runs through every AI you've connected** — OpenCode, Gemini, OpenRouter, ChatGPT, Ollama, your custom endpoint — one at a time. The answers open **side by side in a compare window**, each labeled with the AI, the model, and how long it took; **✓ Use this one** sends the winner back to the Lab (for a beat answer, it builds the beat). AIs with no key saved yet are skipped with a note saying so.
- **fal.ai online provider** (paid, labeled as such everywhere): a third ☁️ Online compute choice next to Replicate (paid) and Hugging Face (**free** tier) — signup and API-key buttons included, key stored encrypted. fal.ai runs **ACE-Step songs** in Create Song and **Demucs stems** in the Stem Splitter.
### Changed
- Online providers are labeled **paid / FREE** right in the picker, so it's always clear what a run costs before you make it.

## Version 3.12 — Downloads, Categories & a Stop That Stops
### Fixed
- **Stop actually stops**: sounds triggered on the timeline used to ring out to their natural end after you hit Stop. Every voice is now tracked and cut with a fast fade the moment Stop is pressed.
### Added
- **⬇ Downloads window** (View menu / library button): every download in one place — file size, **live speed, time remaining**, and **pause / resume and cancel** per download. The AI model download and Freesound sounds run through it.
- **Library categories**: every sound is filed automatically — 🥁 Drums, 🎸 Guitar, 🔊 Bass, 🎹 Keys & Pads, 🔁 Loops, 🎤 Vocals, ✨ FX, 🎵 Other (plus 🎬 Video / 🖼️ Images) — with a **category dropdown and a search box** at the top of the library. Downloads, pack sounds and renders all land in the right category, and categories save inside your project files.
- **📼 Cassette platters**: each DJ deck's Style row switches the turntable from Vinyl to a **Cassette tape** — reels spin with the music and the tape visibly moves from the left reel to the right as the song plays.
- **🎨 Visual colors**: the same Style row sets the color theme for that deck's visuals — Deck Color, 👑 Gold, Green, Purple, Ice, Sunset, Pink, White — and it retunes the Rainbow Spectrum too. Platter and color choices are remembered per deck.
- **🥁 Auto-make the beat** (Create Song): builds a beat in your chosen style with the drum generator at your BPM — it lands as an editable pattern and a library loop to layer the generated song over.
- **Suno-style preset chips** (Create Song): one tap fills the style box with a proper Suno-style prompt — 👑 My Style, Phonk, Trap, Lo-Fi, Country, Country Rap, Rock, EDM, Synthwave, Soul/Gospel, Blues, Folk — built from Suno's own style list. The **drum generator learned five of them too**: Phonk, Dubstep, Synthwave, Afrobeat and Country Train (17 generator styles now).
### Changed
- **AI Lab decluttered**: two tabs — **✨ Create** for making things, **🔑 Keys & Compute** for API keys and compute settings.
- **Freesound decluttered the same way**: a clean **🔎 Search** tab up front; the API key lives in its own **🔑 Connect** tab.

## Version 3.11 — Create Song + Free Online Tier
### Added
- **🎤 Create Song** (View menu / command palette / "Create Song from this…" in the AI Lab): the personal-Suno panel — title, style, lyrics, and **Style Influence, Voice Influence and Weirdness sliders**, a **Voice picker** (generated voice or 👑 Rad-Dad — my voice), song length, and the big **Generate Song** button. Routing follows your Compute choice: **☁️ Online via Replicate runs ACE-Step** (lyrics + style in, finished song into your library); the **local engine** on your stronger PC plugs into the same button when it's set up, and your Rad-Dad voice conversion is the stage that runs there once your voice model is trained.
- **Online provider choice**: Replicate (paid, fast) or **Hugging Face (free tier)** — pick it in the AI Lab's Compute panel; the Stem Splitter's ☁️ Online route uses whichever you chose. Local CPU/GPU remains the always-free, always-works option.

## Version 3.10 — The Built-in Sound Pack
### Added
- **🎁 Sound Pack** (View menu / command palette): a starter library of **87 sounds built right into the app** — 38 drums & percussion (five kicks, four 808s, five snares, claps, hats, crashes, toms, shakers, congas…), guitar plucks, chord stabs and muted hits, three strummed guitar loops, bass subs/plucks/growls, piano chords and pads, four drum loops, and FX (riser, impact, vinyl crackle). Every sound was synthesized for BeatSync and is **free to use in your music — no credit, no catches**. The whole pack lives in **one 17 MB compressed file** the app reads directly; sounds only take real space when you add them to your library, and from there they work everywhere — kits, patterns, timeline — and save inside your projects.

## Version 3.9 — Sounds from the Internet
### Added
- **🌐 Freesound Sounds** (View menu / command palette): search freesound.org's massive library of licensed samples from inside the app — drums, loops, textures, anything. **Preview** any result, then **⬇ Add to Library** and it's yours: it shows in the drum-kit channel pickers, patterns, and the asset list, and saves inside your project. Every result shows its **license and author** so giving credit is easy. Your free Freesound API key (freesound.org/apiv2/apply) is stored encrypted on this PC.
- **Replicate signup buttons** in the AI Lab's ☁️ Online compute panel: "Sign up for Replicate (free)" and "Get my API token" open the right pages in your browser, so setting up online compute takes a minute.

## Version 3.8 — Your Kits, Your Compute
### Added
- **🎒 Kit from Files** (step sequencer): pick your own WAV/MP3 files and each one becomes a drum channel in a fresh pattern, ready to play. The files join your library, show in every channel picker, and **save inside the project file** — your kits travel with the song.
- **Compute choice: Auto / CPU / GPU / ☁️ Online** (AI Lab → Compute), with the graphics devices on the PC listed by name. It drives the **Stem Splitter** today: run it on the **CPU**, on the **GPU** (DirectML — works with NVIDIA and most modern cards), or **online**, where **Replicate's** cloud runs Demucs for you (your own Replicate key; a few cents a song, key stored encrypted). Auto picks the GPU when a real graphics card is present and stays on CPU for integrated graphics. The same switch will run the song-generation engine when it's set up.

## Version 3.7 — A Visual for Every Mood
### Added
- **Eleven deck visuals** (the Visual picker on each deck): **Spectrum**, **Rainbow Spectrum** (LED blocks), **Spectrum + Peaks** (falling white peak caps), **Mirrored Spectrum**, **Radial Spectrum** (bars radiating from a circle), **VU Bars** (green / yellow / red zones), **Waterfall** (history scrolling down), **Wave Fill**, **Waveform**, **Level + Bass** (two big meters), and **Falling Dots** — plus Off. Every spectrum mode uses the auto-gain from 3.6, so they all dance at any volume.
### Changed
- **My Songs is now per-user, the way it should be**: the panel no longer preloads anyone's songs. Each user signs into **their own** Suno / SoundCloud account in the player (the sites handle the login; the app never sees a password, and the session stays signed in on that PC). The songs on the page being viewed appear in a **"Songs on this page"** list — click one and it plays, with its **cover photo and lyrics** shown underneath when the page has them. The hand-built quick list stays, saved locally per user.

## Version 3.6 — Decks That Fit, Spectrum That Works
### Fixed
- **The DJ window fits your screen now**: it clamps itself to the screen's work area when it opens (scaled displays were clipping it on the right and bottom), the platters render a little smaller, and the track lists are tighter — everything is reachable without hunting for the scrollbar.
- **The deck Spectrum actually shows now**: the raw levels run small, so the bars were rendering at a pixel or two and looked dead. Both spectrum modes now auto-gain to the song's real level.
### Added
- **🌈 Rainbow Spectrum** deck visual — Michael's pick: stacked LED-style blocks sweeping red → purple → blue → green → yellow → orange across the bands, alongside the deck-color Spectrum, Waveform, and Off.
### Changed
- **"Created by Michael DeCato" is back on the splash screen** (still off the main window and Start Screen).
- **AI Lab default model**: the OpenCode provider now starts on **nemotron-3-ultra-free** — the strongest of the free models, and the recommended pick.

## Version 3.5 — The Timeline, Restyled
### Improved
- **The timeline now wears the feature page's look**: clips are matte and muted instead of neon — **violet** for video, **teal** for the separated video audio, **deep green** audio on even tracks and **slate blue** on odd tracks, so neighboring tracks are easy to tell apart. A clip's color follows it when you drag it to another track.
- **Waveforms read properly**: the waveform inside each clip is now drawn in soft white at half strength (it used to be dark green on dark green), so you can actually see the shape of the sound the way the mock showed it.
- **Glowing playhead**: the playhead is the page's bright green with a soft glow, handle included.
- **The beat grid sits back**: gold lines are fainter (downbeats still a touch stronger), so the grid guides without shouting over your clips.
- **The A–B loop is visible on the timeline now**: a soft gold band across the lanes with a solid gold brace along the top, exactly like the mock — set A and B and you can see the loop region, not just the buttons.
- Lane labels use a compact mono font, and the video lane's accents are muted violet / teal to match its clips.

## Version 3.4 — AI Lab, My Songs & Real Deck Mixing
### Added
- **AI Lab** (View → AI Lab): connect an AI and generate **song lyrics**, **title ideas**, or a **beat from your description** — a generated beat opens straight in the step sequencer. Providers: **OpenCode's free models** (uses your OpenCode app sign-in, no key), **Ollama** (runs 100% on this PC), **Google Gemini** (free key), **OpenRouter** (free models), **ChatGPT / OpenAI** (your own key), or any **custom OpenAI-compatible endpoint**. Keys are stored encrypted on this PC.
- **My Songs** (View → My Songs): all 17 published Rad-Dad songs in one panel — pick one and it plays right in the app (SoundCloud links play in an embedded player too), or open it in your browser. Add and remove songs anytime; your list is saved on this PC. Streaming stays listening-only: to edit a song's audio, import the file — the app never logs into Suno or downloads from it.
- **Deck track lists**: every DJ deck now has its own **track list** — add songs (multi-select), double-click or use Prev / Next to load them, and tick **Auto-play next** to roll into the following track when a song ends. Queue up each deck's set and crossfade between them.
- **Per-deck visuals**: each deck shows its **own** live sound under the waveform — **Spectrum**, **Waveform**, or **Off** — so you can see what each deck is doing while you mix.
### Fixed
- **Scratch now follows your hand**: hand speed is measured over a short window instead of instant-by-instant, so a steady drag sounds like the record moving (slow drag = slow motion), and the old "it just pauses" behavior is gone. Letting the record sit still still holds it silent, like a real deck.
### Changed
- The "Created by" line no longer sits on the splash screen, the Start Screen, or the main window — it lives in the About box, where it belongs.

## Version 3.3 — Scratch & the Waveform Ring
### Added
- **Scratch mode** (per deck): an ✋ Scratch toggle — **off by default**, so the record can't be grabbed by accident. Turn it on, grab the vinyl with the mouse and move it: the audio follows your hand forward and backward, goes silent while you hold the record still, and spins back up to speed when you let go — just like a real deck.
- **Waveform ring**: each platter now carries the song's waveform as a ring of bars inside the neon rim. The played part lights up in the deck color, the playhead bar is white, and the ring and the platter's glow pulse with the beat as it plays.
### Fixed
- **MP3 loading is more robust**: files now decode through Windows Media Foundation first (with the classic MP3 reader as backup), and a file that decodes to almost no audio now says so instead of loading as an empty song.
- **Decks can no longer be frozen by the timeline's pause**: pressing a deck's Play now resumes the shared output if it was paused, so the turntables always play when you hit play.

## Version 3.2 — Your Style, More Drums, Turntables
### Added
- **Seven new built-in drum sounds** — open hat, rimshot, shaker, low tom, high tom, cowbell, and crash. The step sequencer now has 12 built-in drums.
- **Six new drum generator styles** — Lo-Fi, UK Drill, Dancehall, West Coast / G-Funk, Techno, and Drum & Bass.
- **Unlimited render length**: the sequencer and piano roll bar pickers are now type-any-number (presets up to 128 bars, type up to 999) — renders start at 4 bars and go as long as you want.
### Improved
- **Boom-Rap, labeled right and kept separate**: the Rad-Dad style is now called **Boom-Rap** (not Boom-Bap) and lives in its own **⭐ My Styles** group in the Drum Generator, apart from the general styles. The sequencer's starter pattern is now the "Boom-Rap Groove", and the Starter Loop Pack leads with a Boom-Rap loop (six loops now).
- **DJ Decks look like turntables now**: each deck has a neon-ringed vinyl platter in its deck color — green for A, purple for B — modeled on the splash screen, with groove rings, a label, a tonearm, and a marker dot. **The record spins at 33⅓ RPM while the deck plays** (and scrubs when you drag the waveform); click the vinyl to play/pause. The mixer gained the splash's green EQ strip, and the whole window wears the app's navy gradient.

## Version 3.1
### Added
- **Changelog viewer** (Help → Changelog) — this screen. The changelog now lives inside the program, so after any update you can read exactly what changed.

## Version 3.0 — The Professional Look
### Improved
- **Brand-new main window design**: brand bar with the app icon, two-tone wordmark, version chip, and creator credit; deep navy gradient background.
- **Rounded buttons everywhere** with hover and press feedback; **fully dark menus** (the stock light-gray popups are gone), larger transport controls, and a glowing green time readout.
- Every feature works exactly as before — this update is the look, not the engine.

## Version 2.19 — Drum Generator & Starter Loops
### Added
- **Drum Generator** (View menu): six styles — Boom-Bap, Trap, House, Funk, Rock, Dembow — with a density control and humanized hit strengths; generates fully editable patterns.
- **Starter Loop Pack**: four ready 4-bar loops (boom-bap, trap, house, funk) added to your library with one click.
### Improved
- Patterns now support **per-step velocities** (hit strength), honored in sequencer playback and renders.

## Version 2.18 — Video Export
### Added
- **Export Video is real**: renders your full timeline mix (mastering choices included) and combines it with the timeline video into an MP4 (H.264 video + AAC audio). The video's trim, speed, and timeline position are honored. First use asks to download ffmpeg once (free, about 30 MB — it is never bundled into the app).

## Version 2.17 — Keep Pitch
### Added
- **Keep pitch at this speed** (per clip, right-click menu): slow a clip down or speed it up without the chipmunk / deep-voice pitch shift, using a WSOLA time-stretcher. Works in playback and exports.

## Version 2.16 — Envelopes, Gate & Spectrogram
### Added
- **Volume envelope editor** per clip, with presets: Fade In, Fade Out, Duck Middle, Swell — draw your own points too.
- **Noise gate** per clip: silences hiss and room noise below a threshold you set.
- **Spectrogram view** per clip: see hiss, hum, and harshness as a heat map.

## Version 2.15 — Piano Roll
### Added
- **Piano Roll** (View menu): click notes onto a 2-bar grid (C3–B4), note lengths of 1/2/4/8 steps, four instruments — Keys, Sub Bass, Pluck, Melodic 808. Live loop playback and Render to Timeline.

## Version 2.14 — Step Sequencer
### Added
- **Patterns / Channel Rack** (View menu): 16-step sequencer with built-in synthesized drums (kick, snare, hi-hat, clap, 808) or any sound from your library. Multiple named patterns, saved in the project, with sample-accurate Render to Timeline.

## Version 2.13 — Stem Splitter
### Added
- **Stem Splitter** (View menu): splits a clip or an audio file into drums, bass, other, and vocals WAV files using the HTDemucs AI model (one-time download of about 304 MB, asked first).

## Version 2.12 — Transcript
### Added
- **Transcript pane** (View menu): speech-to-text for any clip (Whisper AI, one-time model download). Click a line to loop that exact moment; save as .txt or .srt subtitles. Note: it understands speech far better than sung vocals.

## Version 2.11 — Per-Clip FX
### Added
- **Clip FX**: every clip gets its own EQ / delay / reverb chain (right-click a clip → Clip FX), separate from the track FX.

## Version 2.10 — Render Queue & Batch Converter
### Added
- **Render Queue** (File menu): line up several exports and render them one after another.
- **Batch Converter** (File menu): convert a batch of WAV/MP3 files, with optional loudness normalization to −14 LUFS.

## Version 2.9 — Start Screen
### Added
- **Start Screen**: project templates (Beat Session, Song + Vocals, Video Soundtrack, DJ Prep) and a recent-projects list. Can be reopened anytime from the File menu.

## Version 2.8 — Command Palette & Hotkeys
### Added
- **Command Palette** (Ctrl+K): every feature in the app, searchable from the keyboard.
- **Hotkeys**: Ctrl+S save, Ctrl+O open, Ctrl+E export, Ctrl+M mixer, Ctrl+T add track, Space play/pause, A/B loop points, L loop toggle, F1 about, Esc stop.

## Version 2.7 — Export Bundles & Filename Patterns
### Added
- **Export bundles**: Streaming, Loud, Vocal, Warm, and Clean presets that set mastering and loudness for you.
- **Filename patterns** with tokens ($project, $date, $time, $bpm, $preset) and a live preview; your export settings are remembered between sessions.

## Version 2.6 — Beat Grid
### Added
- **Automatic beat detection**: analyze any clip to find its BPM and first beat, draw the gold beat grid on the timeline, and snap clips and loop points to the beat.

## Version 2.5 — Mastering on Export
### Added
- **Mastering presets** — Universal, Fire, Clarity, Tape — with an intensity slider, plus optional loudness normalization to −14 LUFS (streaming standard), measured with proper K-weighted metering.

## Version 2.4 — A–B Loop & Monitor FX
### Added
- **A–B loop**: set loop points and loop any section of the timeline.
- **Monitor FX** (View menu): an EQ + compressor + stereo-width strip on what you hear, with presets (Flat, Vocal Clarity, Bass Boost, Wide Stage). Monitor only — never baked into exports.

## Version 2.3 — Per-Clip Tools
### Added
- **Reverse**, **Normalize**, and **Nudge** (fine timing shift, ±5 seconds) per clip, plus **automatic crossfades** where clips overlap on the same lane.

## Version 2.2 — Output Visualizations
### Added
- **Visuals on the Output window**: spectrum bars, wave scope, and pulse rings driven live by the master audio, with an auto-cycle mode.

## Version 2.1 — DJ Decks
### Added
- **DJ Decks** (View menu): two decks with tempo faders and SYNC, TAP tempo, four hot cues each, beat loops, per-deck FX, a crossfader with three curves, and mix recording to WAV.

## Version 2.0 — Output Window
### Added
- **Output window** (View menu): a detachable stage screen — send the video (or black, or visuals) to a second display, fullscreen, always-on-top.

## Version 1.9 — Full-Length Songs, Playhead & Menus
### Added
- **Clips now play their full natural length** — whole songs, not 60-second cuts.
- **Bright green playhead** that scrolls the view as it plays, a **time display** (current / total), and a proper **menu bar** (File / Import / Tracks / View / Help).

## Version 1.8 — Audio Export
### Added
- **Export your mix** to WAV or MP3 (192 kbps), with a progress window and cancel.
### Fixed
- Hotfix: a startup error on stereo material ("Source sample provider must be mono") — the pan control was replaced with a stereo-safe one.

## Version 1.7 — Track FX
### Added
- **Per-track effects**: 3-band EQ, feedback delay, and reverb on every track (via the Mixer's FX button).

## Version 1.6 — Mixer
### Added
- **Mixer window**: per-track volume faders and pan knobs, plus a master strip — adjustable live while playing.
### Fixed
- **Pause now actually pauses**: the play clock, the audio, and the video freeze together.

## Version 1.5 — Open Project
### Added
- **Projects reopen properly**: Save and Open round-trip through the .bsp project file with all assets bundled inside. A ready-made demo project is included.

## Version 1.4 — App Icon
### Added
- **Custom neon app icon** for the window, taskbar, and installer shortcuts.

## Version 1.3 — Video Speed & Real Trims
### Improved
- **Video playback speed** (0.25×–3×) that video blocks fully respect.
- **Edge drags are now true trims**: they skip into the sound and shorten the window, instead of just resizing the box.

## Version 1.2 — Video Audio Lane
### Improved
- Separated video audio now lives on its **own dedicated lane** directly under the video, locked in place so track changes can't delete it.

## Version 1.1 — Video Audio Separation
### Added
- **Separate audio & video**: pull a video's soundtrack out as an editable audio clip (or extract it to the library); the video itself goes properly mute.

## Version 1.0 — First Working Build
### Added
- BeatSync Studio Pro compiles and runs: **asset library** (video, image, audio, MIDI, and ZIP import — ZIPs arrive as grouped packs), **drag-and-drop multitrack timeline** (up to 8 audio tracks + video + image clips), per-clip **volume and speed**, master volume, a **splash screen**, and Save/Open project groundwork.
