# BeatSync Studio Pro — Changelog

Every update since the first working build: what was added, improved, and fixed.
Created by Michael DeCato.

## Version 3.120 — Transport labels: STOP / PREV / NEXT / PFL fully readable 🔎
- **Fixed clipped transport labels:** the red STOP button read "STOI" and PFL read "PFI" — the app-wide button padding left too little room for the icon + text at those widths. STOP and PFL are wider now with tighter padding, so the full label shows with room to spare.
- **Skip buttons are real labeled buttons now:** "⏮ PREV" and "NEXT ⏭" flank PLAY at the same size as CUE/SYNC, instead of small bare icons that were hard to make out. Same stepping as before.
- No behavior changed: Stop still stops the deck and rewinds to cue, Reset still resets to defaults, and the skips still step through the deck's track list.

## Version 3.119 — Deck transport: Stop + Reset + Skip, and breathing room 🎧
- **NEW: ⏹ STOP button on each deck:** stops that deck and rewinds it to its cue point — the same move "Stop Both Decks" in the mixer already did, but for just the one deck.
- **NEW: ⟲ RESET button with the transport:** reset-to-defaults (tempo 0%, original key, EQ flat, gain + fader at unity, loop/scratch/slip off) now sits in the transport row on each deck, next to Stop — the small header Reset is still there too.
- **NEW: ⏮ ⏭ skip buttons flanking PLAY:** step backward/forward through the deck's track list, left and right of the play button where your hand already is. Same stepping the track-list Prev/Next buttons use; if the list is empty the status line says so instead of the buttons silently doing nothing.
- **Fixed the smashed-together deck rows:** the Key row was ~476px wide in a 335px column, so Key Lock and Suggest ran underneath the right-hand column — it's now two rows and everything is visible. The auto-gain loudness readout got its own line under the button so the longer LUFS text can't clip at the deck edge, the tempo slider gave the 0% button its room back, and long stem status messages now trim instead of pushing the row over. No control was removed to make room.

## Version 3.118 — Auto-Level: true loudness matching + leveled encodes 🔊
- **Auto-gain now measures real loudness (LUFS):** each deck's auto-gain uses K-weighted integrated loudness (the same measurement the batch converter already uses) instead of plain RMS, targeting -14 LUFS with the peak capped at -1 dB — quiet songs come up, loud songs come down, and every track plays at the same volume without touching the knob.
- **Measured once, remembered:** loudness is cached by file in the app settings folder (loudness-cache.json), so a song is only measured the first time it loads — later loads on either deck pick up the stored level instantly.
- **Per-deck readout:** the auto-gain label now shows the measured loudness and the trim being applied (e.g. "auto +1.7 dB | -15.7 LUFS"), next to the existing per-deck Auto-Gain ON/OFF switch.
- **NEW: "Apply Auto-Level" on Encode:** the Encode window has a checkbox (on by default when the deck's auto-gain is on) that bakes the same -14 LUFS leveling into the exported file, so the file itself plays evenly anywhere — truck, phone, anywhere. The original file is never changed, and leaving it unchecked gives the same raw encode as before.
- **No double-boost:** a leveled export played back with auto-gain on already measures right, so it gets ~0 dB of extra trim.

## Version 3.117 — Scene Cut / Paste (DJ Sync Pro idea, built into BeatSync)
- **New ✂️ Scene Cut / Paste window** (menu: **Video → Scene Cut / Paste...**): load a video, build a list of named scenes with start and end seconds, then **Cut, Copy, Paste, Delete, Move Up, Move Down** to reorder them and **Export** once. A scene can come from a different clip file, so outside footage (a Flow clip, a Kling clip) drops into the order without touching the main video.
- **The song audio stays locked:** export renders the reordered picture in one ffmpeg pass, then muxes the FULL original audio untouched on top. A running total is shown against the locked song length, with a plain warning (not a silent break) when the scene total does not match it.
- **Green Mountain State preset built in:** the "GMS lyric moves" button fills the lyric-move order — intro to 49s, Flow mud 49–57s before the barn, barn-through-throne 57–225s, Kling finale 220–225s, crowd cheering to the end — totalling exactly 236.833s, so the next lyric move can be done in the app instead of by hand.

## Version 3.116 — AI Toolkit: shared knowledge in every tab
- **New 🧠 Shared knowledge in the AI Lab**: every tab now answers with the knowledge Michael and Jarvis built together — boom-rap style and voice rules, the v2-is-the-video and action-clip and finale specs, bare-arms and crown rules, pool roles, and the standing don'ts — injected once in the shared provider layer, so no tab prompt changed and all 13 tabs pick it up at once. The pack lives in the app itself (no sidecar file to lose) and contains no passwords, keys, or personal details by design.
- **Fixed 🖥️ Splash screen version**: the splash screen showed the wrong version (3.65) — it now shows the real version, 3.116, in the same build.

## Version 3.115 — AI Toolkit, Part 13: Dashboard
- **New 🏠 Dashboard tab in the AI Lab** (thirteenth of the AI Toolkit, built one tool at a time): a card grid home for the whole Lab — one card per tool, from 🎤 Song through 📣 Social Kit. Tap a card and it jumps straight to that tab; the work itself still happens there, with the provider and model picked on the 🎤 Song tab.

## Version 3.114 — AI Toolkit, Part 12: Social Kit
- **New 📣 Social Kit tab in the AI Lab** (twelfth of the AI Toolkit, built one tool at a time): give it a **song title** and **what the song is about**, and the AI writes ready-to-post copy in three sections — a **YouTube SEO description** (title line, description, tags), a **Facebook / Instagram caption**, and a **TikTok hashtag strategy**.
- Every section is yours to edit, with **Copy YouTube**, **Copy Caption**, **Copy Hashtags**, and **Copy All** — the whole kit at once, labeled and ready to paste anywhere. It writes copy, not lyrics, so there is no Create Song carry-over on this tab.

## Version 3.113 — AI Toolkit, Part 10: Cover Art
- **New 🎨 Cover Art tab in the AI Lab** (tenth of the AI Toolkit, built one tool at a time): describe the **concept**, pick an **art style** and a **shape**, and generate the artwork — with **two engines**, per Michael's call: **Local — Dayz Stable Diffusion** (free, on his own GPU) or **Image API** (paste an endpoint + key; the API side expects an OpenAI-compatible images endpoint, and a pasted key is stored encrypted on this PC).
- The **final prompt** is yours to edit — Generate uses your edited prompt if you changed it. **💾 Save Artwork** saves the image as a PNG. Honest scope: generated art is preview-size — a RouteNote cover still needs an exact 3000×3000 master.

## Version 3.112 — AI Toolkit, Part 11: MV Director
- **New 🎬 MV Director tab in the AI Lab** (eleventh of the AI Toolkit, built one tool at a time — Cover Art is skipped for now until the image engine is picked): give it a **theme** and your **lyrics**, and the AI writes a music video treatment — the **concept**, the **art direction** (look, lighting, camera, color), and a **timed shot-by-shot script** that follows your words.
- Every section is yours to edit, and **📋 Copy Full Treatment** grabs the whole thing at once, labeled and ready to paste anywhere.

## Version 3.111 — AI Toolkit, Part 9: Chords
- **New 🎹 Chords tab in the AI Lab** (ninth of the AI Toolkit, built one tool at a time): paste your **lyrics**, pick a **mood**, and the AI writes a chord sheet — a **key** that fits the mood and words, a **verse progression**, a **chorus progression**, and **suggested instruments** to play them on.
- Every section is yours to edit, and **📋 Copy Chord Sheet** grabs the whole sheet at once, labeled and ready to paste anywhere.

## Version 3.110 — AI Toolkit, Part 8: Artist Persona
- **New 🎭 Artist Persona tab in the AI Lab** (eighth of the AI Toolkit, built one tool at a time): type an **artist name** and a few **style hints**, and the AI builds a full persona — **genre tags**, **vocal style**, **visual style** (stage look, cover aesthetic), a **Suno prompt**, and a **backstory / bio**.
- Every section is yours to edit. **📋 Copy Suno Prompt** grabs just the prompt, and **🎤 Use This Style in Create Song** carries it into Create Song with the lyrics left blank for you to fill — so the style is yours and the words are yours.

## Version 3.109 — AI Toolkit, Part 7: Style Generator
- **New 🎛️ Style Generator tab in the AI Lab** (seventh of the AI Toolkit, built one tool at a time): pick a **base genre** and a **mood**, add **keywords** (like "808, banjo, Vermont, night drive"), and the AI writes **three style-prompt variations** — each one a full, usable prompt for a song generator, each different enough to choose between.
- Every variation is yours to edit. Pick one with the **Use variation** picker, then **📋 Copy Selected** or **🎤 Use This Style in Create Song**, which carries the picked variation into Create Song with the lyrics left blank for you to fill — so the style is yours and the words are yours.

## Version 3.108 — AI Toolkit, Part 6: Vision to Style
- **New 👁️ Vision to Style tab in the AI Lab** (sixth of the AI Toolkit, built one tool at a time): pick an image and the AI describes what it sees, then writes a **style prompt**, a **title idea**, and **mood tags** for a new song in that picture's mood. Add optional notes (like "make it boom-rap, Vermont, family") to steer it.
- **How it works, honestly:** moondream (local Ollama, free) looks at the image, then the model you picked in the 🎤 Song tab writes the style, title, and mood tags from what moondream saw. Two steps on purpose — the small vision model describes a picture fine but won't write four formatted sections in one call, so each model does the job it's good at. The picture never leaves your PC. The style is yours to edit, with **📋 Copy Style**, **📋 Copy Title**, and **🎤 Use This Style in Create Song**, which carries the style into Create Song with the lyrics left blank for you to fill.

## Version 3.107 — AI Toolkit, Part 5: Hit Analyzer
- **New 📊 Hit Analyzer tab in the AI Lab** (fifth of the AI Toolkit, built one tool at a time): paste lyrics, add optional style/genre context, and the AI scores the song **out of 100**, names **what's working**, flags the **issues found**, gives a **genre trends read**, and hands back a **polished draft with the fixes applied** — yours to edit, with **📋 Copy Polished Lyrics** and **🎤 Make THESE Lyrics into a Song** into Create Song with your standing boom-rap style and voice ready.
- **Said plainly:** the score is the AI's opinion on the lyrics as written — not a chart prediction and not streaming data — and the trends read is the model's genre knowledge, not live chart data.

## Version 3.106 — AI Toolkit, Part 4: Youtube to Style
- **New ▶️ Youtube to Style tab in the AI Lab** (fourth of the AI Toolkit, built one tool at a time): paste a YouTube link, hit **🔍 Read Video**, and BeatSync pulls the video's title and creator — **no YouTube key needed** (it uses YouTube's free oEmbed lookup). Add optional notes about what you hear (instruments, tempo, mood, what it's about), and the AI writes a **style prompt** plus a **lyrics concept** for a NEW song in that style.
- **The style is the sound; the concept is only an idea — no lyrics are copied from the video.** The style prompt and concept are yours to edit, with **📋 Copy Style**, **📋 Copy Concept**, and **🎤 Use This Style in Create Song**, which carries the extracted style into Create Song with the lyrics left blank for you to fill — so the style is yours and the words are yours. If a link can't be read (private video, bad link, no network), the tab says so plainly and you can still generate from your notes alone.

## Version 3.105 — AI Toolkit, Part 3: Lyrics Rewriter
- **New 🔄 Lyrics Rewriter tab in the AI Lab** (third of the AI Toolkit, built one tool at a time): paste lyrics, pick a **Rewrite goal** (fresh take, different mood, shorter and punchier, bigger chorus, more Vermont country-rap edge) and add an optional **Direction** note (like "keep the truck line"), and the AI writes a new draft — same idea, new words — plus a **style line**.
- **Same plumbing as the tabs before it:** the provider and model pickers from the 🎤 Song tab, so local Ollama, OpenCode's free models, or your OpenAI-compatible services all work with no new setup. The rewritten lyrics are yours to edit, with **📋 Copy Rewritten Lyrics** and **🎤 Make THESE Lyrics into a Song**, which carries them into Create Song with your standing boom-rap style and voice ready. A rewrite is a new draft, not a polish — the ✨ Optimizer tab is the one that keeps your lines.

## Version 3.104 — AI Toolkit, Part 2: Lyrics Optimizer
- **New ✨ Lyrics Optimizer tab in the AI Lab** (second of the AI Toolkit, built one tool at a time): paste lyrics you already have, add **Meta Tags** (style tags like melancholic piano, swelling strings, mid-tempo, 90 BPM) and **Vocal Coaching** notes (delivery like clear male vocal, emotive, building intensity), and the AI polishes them — same story and meaning, tighter flow, rhyme and singability — plus an **optimized style line**.
- **Same plumbing as Story to Lyrics:** the provider and model pickers from the 🎤 Song tab, so local Ollama, OpenCode's free models, or your OpenAI-compatible services all work with no new setup. The optimized lyrics are yours to edit, with **📋 Copy Optimized Lyrics** and **🎤 Make THESE Lyrics into a Song**, which carries them into Create Song with your standing boom-rap style and voice ready.

## Version 3.103 — AI Toolkit, Part 1: Story to Lyrics
- **New 📖 Story to Lyrics tab in the AI Lab** (first of the AI Toolkit, built one tool at a time): type a story, concept or idea, pick a **Genre**, a **Mood** and a **Song Structure**, and the AI writes the full song — lyrics plus a **style suggestion** and **suggested titles**, laid out the way the reference toolkit does it.
- **It reuses the AI Lab you already have:** the provider and model pickers from the 🎤 Song tab, so local Ollama, OpenCode's free models, or your OpenAI-compatible services all work with no new setup. The lyrics box is yours to edit, with **📋 Copy Lyrics** and **🎤 Make THESE Lyrics into a Song**, which carries them into Create Song with your standing boom-rap style and voice ready.
- **Your voice stays yours:** every draft is written as Rad-Dad boom-rap — heavy 808, honest small-town Vermont life, family, trucks, hard work, no explicit content. Genre and mood shape the story's feel; they don't replace your style.

## Version 3.102 — Deck Visuals Fixed + Reset-to-Defaults
- **The deck visuals actually dance now.** Root cause found by testing the live board: the deck spectrum's auto-gain floor was 6x higher than the (working) top master strip's, so every one of the 12 visual styles rendered squashed into the bottom quarter of its strip and looked dead. The deck scaling now matches the master strip's proven recipe, the radial style reaches the full strip height, and the Spectrum+Peaks caps fall at a visible rate instead of lingering.
- **New per-deck "Reset" button** (top-right of each deck, beside Tags): one click restores tempo 0%, the original key (any key shift rendered back), clears a hand-typed BPM, flattens the channel EQ, puts gain + fader back at unity, and turns loop / scratch / slip off. The song stays loaded.
- Verified live on the board: Play/Pause, the Visual picker (all 13 choices), Quantize, Scratch and Slip toggles, channel strips and the top spectrum all respond; the deck strip fault was the scaling above, not the picker.

## Version 3.101 — Radio Drops: Empty Pads Explain Themselves
- **Fix for "the radio drops don't do anything":** the pads ship unassigned on purpose (they're for YOUR voice tags), but clicking an empty pad only printed a faint status line — so they looked broken. Now **clicking an empty pad opens the sound picker immediately**: pick any WAV/MP3 and that pad is live. The pads also say "click: assign a sound" on their face.
- **Event Mode says it out loud too:** tapping an unassigned drop there now flashes a big amber warning naming the pad and where to assign its sound, instead of silence.

## Version 3.100 — Vermont Station Directory + Search
- **Find Stations, built into the Internet Radio window:** a **Vermont directory** of 14 stations, every one stream-tested live before it made the list — Rutland's Rock 94.5, Z97.1 and Cat Country 105.3, Vermont Public (News, Classical, Jazz, BBC), WDEV Waterbury, The Point Montpelier, WRUV Burlington, WGDR Plainfield, WMRW Warren, Alt Vintage, and your own Rad-Dad Radio. Each is listed by **name and location**, as asked.
- **Search three ways:** by station name, by town, or by style (country, jazz, rock…). An empty search browses the whole state.
- **Online search too:** the same box also queries the free Radio Browser directory (no account, no key) for any station name anywhere — results merge in, listed by name and state/country.
- **One click to keep:** double-click any result (or Add selected) and it joins My Stations permanently.

## Version 3.99 — Internet Radio (Listen Live)
- **New 📡 Internet Radio** (button under Event Mode in the DJ board): listen to live online radio stations through the DJ mix — the station plays over the board and lands in Record Mix, Record My Set and the live stream like everything else.
- **Your local stations are built in:** **Rock 94.5 — WDVT** (Rutland classic rock) and **Z97.1 — WZRT** (Rutland Top 40), using the direct streams from the stations' own web players (found and verified live), plus **Rad-Dad Radio**, your own Icecast. Double-click a station to play it.
- **Now playing, from the station itself:** when a stream carries ICY song titles, the window shows the song the station is playing right now, updating as songs change.
- **Add any station:** name + direct stream address, saved to your station list. Remove works too. Playback is buffered on its own thread, so a network hiccup plays a moment of silence instead of ever glitching the mix.

## Version 3.98 — Event Mode
- **New 🎉 Event Mode** (button under Radio Drops in the DJ board): a full-screen, big-button control surface for running a live event — the haunt, the parade, a party — built for the dark and for gloves.
- **What's on it, and nothing else:** Now Playing with progress, one giant **Play/Pause**, a giant **Next Song** (with the next track's name under it), three crossfader jumps (Deck A / Both / Deck B), a giant **Mic** live/mute switch, and the 8 **radio drops** (which duck the music, same as the Drops board).
- It drives the same decks, queues, mic and drops as the main board — queue your playlist on a deck as usual, open Event Mode, and run the whole night from the big screen. Exit any time; the DJ board stays open behind it.

## Version 3.97 — Radio Drops Board
- **New 📻 Radio Drops board** (button under Stream Overlay in the DJ board): a dedicated window of 8 BIG pads for station IDs, Rad-Dad tags and promos — live-radio controls, separate from the DJ sampler's FX pads.
- **Right-click any pad to assign a sound** (any WAV/MP3/FLAC — record your own station ID with the mic, save it, assign it), rename it, or clear it. Assignments are saved and come back next launch.
- **The music dips under a drop automatically** while it sounds (and comes back up when it ends) — composed with the mic auto-duck, so a drop over your voice over music all stack the right way. The board has its own drops volume and a Stop All.

## Version 3.96 — Play-and-Encode (FLAC / MP3 / WAV, your spec)
- **New 💾 ENCODE button on every deck:** encodes the song loaded on that deck to a file in the format YOU pick — **FLAC** (lossless), **MP3** (320 / 256 / 192 / 128 kbps), or **WAV** — at **44.1 kHz or 48 kHz**, 16- or 24-bit for FLAC/WAV.
- **Built for your Suno songs:** stream a song from the Suno tab onto a deck, hit Encode, and the file is rendered from what the deck plays. **Suno's Download button is never used, so your monthly download quota is untouched.** It works for local files too — anything a deck can play, it can encode.
- Encodes land in Music\BeatSync Encodes by default (pick any folder in the window); existing files are never overwritten — the new one gets a (2), (3)… name.

## Version 3.95 — Library Deep-Scan
- **The Song Library's analyze button is now 🔍 Deep-Scan Library:** one click walks EVERY song in the library and fills in whatever analysis is missing — BPM, musical key, and (new) energy — writing BPM/key into the library entries and warming the energy cache.
- **Why it matters:** the Set Builder's energy planner, the key-fit dots, and Suggest Next all read these results. After one deep-scan they answer instantly for your whole library instead of analyzing songs one at a time as you bump into them.
- The scan runs in the background with a progress bar and a plain-language summary (how many BPMs, keys, and energies it filled), it saves as it goes, and **⏹ Cancel Scan** stops it between songs with all progress kept.

## Version 3.94 — Stream Overlay (Now Playing Card)
- **The Output window now carries a stream overlay card** (bottom-left): the song that's actually audible (picked by which deck is playing and where the crossfader sits), the live Rad-Dad Radio listener count from the local Icecast server, and the latest listener request with the requester's name.
- It updates itself every couple of seconds, hides lines that have nothing to say, and is part of the picture Go Live sends to YouTube/Twitch/Facebook — open the Output window and your viewers see what's playing and feel the requests landing.
- **📺 Stream Overlay button** under Go Live turns the card on or off. If the Output window is opened later, it picks up the latest overlay automatically.

## Version 3.93 — Chat Song Requests (Twitch + YouTube)
- **Viewers can now request songs from stream chat:** typing `!request Song Name` (or `!songrequest …`) in your Twitch or YouTube live chat lands in the same Listener Requests list as the web request page — each request shows where it came from.
- **Setup lives in the Requests window** (new Chat Requests panel): Twitch needs only your channel name — the monitor watches chat read-only and anonymously, no Twitch login or password. YouTube needs the live video's ID plus your own free YouTube Data API key, saved encrypted on this PC; BeatSync finds the live chat from the video ID itself.
- Requests from chat carry the viewer's name, respect the same waiting-list limits, and can be dismissed/cleared exactly like web requests. Twitch reconnects by itself if the connection drops; YouTube polls at the interval YouTube asks for and never replays old chat.

## Version 3.92 — Tunable Mic Auto-Duck
- **The mic auto-duck is now a proper radio talk control.** It was already dipping the music when you talked; now it holds the dip through the natural pauses between words instead of letting the music surge back mid-sentence, then releases smoothly when you are done.
- **Three duck depths:** Light (music at 50%), Radio (30%), and Deep (15%), selectable right under the mic controls.
- **A live DUCKING indicator** beside the depth picker shows when the music is actually being held down.

## Version 3.91 — One Menu System, Readable Everywhere
- **Every menu in the app now uses a single dark menu system:** the main menu bar, the timeline clip right-click menu, the sound-library menu, and the DJ board's sound and pad right-click menus all render with the same dark background, light readable text, clear hover highlight, green checkmarks, muted shortcut labels, and dark dividers.
- **What was wrong:** only the main window's own menu had a full dark design; everywhere else the menus fell back to pieces of the light Windows theme — pale popups and highlights with light text on top, the unreadable menus reported on the timeline and in the AI/DJ areas. Those stock pieces are now fully replaced, in one place, so the fix holds in every window.
- The main window's duplicate private copy of the menu design was removed — one design, one place, nothing to drift apart again.

## Version 3.90 — One-Tap Transition FX
- **New on every deck: ⏪ SPINBACK and 🔊 ECHO OUT buttons** (under the transport row) — the two classic DJ exits, one tap each.
- **Spinback** whips the record backwards with an accelerating reverse sweep (about 0.8 seconds) and cuts the deck a few seconds back — the spin-out exit. If the sweep reaches the start of the track, the deck stops there instead.
- **Echo Out** keeps the track playing into a beat-synced feedback delay (a dotted-quarter echo at the deck's BPM) while the dry sound fades out over a third of a second; the echoes ring down for about 2.8 seconds, then the deck stops itself — a clean exit under the next track.
- Both effects run inside the deck's own audio path, so they land in your mix recordings and the live stream too. Pressing Play or Cue (or grabbing the record to scratch) cancels an effect mid-flight.

## Version 3.89 — Set Energy Planner (+ Twitch)
- **New in 🧺 Set Builder: the Set Energy planner.** Under the crate, a live chart draws the set's energy arc in crate order — each song's energy is **measured** (its actual loudness from decoding the file, blended with its tempo), not guessed. Songs are analyzed once and cached, so the chart is instant after the first look.
- **Dots are colored by key fit** with the previous song: green = harmonic, yellow = workable, red = clash, gray = key unknown (Camelot). Reorder songs and the arc redraws immediately.
- **The readout calls the shots:** where the set peaks (and how many minutes in), the biggest energy drop and where it happens, and whether the set builds or winds down.
- **🔺 Ramp by Energy button:** orders the crate quietest → loudest from the measured scores — a ready-made build-up set. Unanalyzable songs keep their order at the end.
- **Also: Twitch joins the Go Live destinations** (address prefilled; key from your Creator Dashboard → Settings → Stream), next to YouTube, Facebook, TikTok, and Custom.

## Version 3.88 — Go Live: Stream the Visuals
- **New: 🔴 Go Live in the DJ window.** BeatSync can now stream itself: the Output (visuals) window — projectM and all — plus the DJ master mix, pushed live to **YouTube, Facebook, TikTok (LIVE Studio RTMP), or any custom RTMP address** through an ffmpeg encoder process.
- **How it works, honestly:** the video is a capture of the Output window exactly as it sits on your screen (keep it visible and uncovered while live — anything covering it covers the stream); the audio is a tap on the master bus that can never stall the music (a backed-up stream drops samples, the mix never does). 720p at 30 fps by default, 480p one click away for an easier ride on the PC.
- **Stream keys, handled right:** paste a key once per destination; it's stored **DPAPI-encrypted on this PC only** and never shown again. YouTube and Facebook addresses are prefilled; TikTok takes the RTMP address from LIVE Studio (TikTok only gives those to eligible accounts).
- **The DJ button shows the truth:** while live it reads 🔴 LIVE with the elapsed time; if the encoder ever dies, the status line says why (full log kept at stream-log.txt in the app data folder).
- **ffmpeg note:** streaming needs ffmpeg (free). If it's missing, this window offers a one-click download; on Michael's PC it's already installed in BeatSyncTools.

## Version 3.87 — Listener Requests for Rad-Dad Radio
- **New: a public request page for the radio.** While the Companion server runs, anyone who can reach this PC at port 8765 /request gets a Rad-Dad Radio page: your name (optional), a song request, and a shoutout/dedication. Send, and it lands in the studio instantly — no account, no password (the DJ controls stay locked behind the companion password as always).
- **Requests live in the DJ window.** The new **📨 Requests** button (under the mic section, next to Companion) shows a live count of waiting requests and opens the requests window: song, who sent it, when, and the shoutout in gold italics. ✔ Done clears a request once played; Clear All wipes the list (with a confirm). Requests are saved to disk, so they survive an app restart.
- **Also on the phone remote:** a Listener Requests card lists what's waiting, read-only, right under the sampler pads.
- **Spam care:** one request per listener address every 15 seconds, a 100-request cap, and sensible length limits on every field.

## Version 3.86 — Phone Remote Grows a Queue
- **New on the phone remote: ⏮ Prev / ⏭ Next for each deck.** The BeatSync Companion page (the phone remote, in the app since 3.32) can now step each deck's track queue from the phone — the same Prev/Next buttons on the deck panels — including loading the first queued song when nothing from the queue is loaded yet.
- **"Next up" on the phone.** Each deck card shows the next queued song's name and queue position (like 2/8), refreshed live with the rest of the remote — see what's coming without walking back to the PC.
- Everything else on the remote is unchanged: play/pause, cue, crossfader, master, cruise, mic, and the sampler pads, all behind the same password.

## Version 3.83 — The Master Spectrum Lives Again
- **Fixed: the master spectrum strip was a permanently black bar.** It drew every tick into a DrawingVisual, but the strip's Image held a DrawingImage captured ONCE at startup over the visual's first Drawing — and every RenderOpen *replaces* that Drawing, so the strip displayed the first (silent) frame forever while the music played. It now presents each frame as a rendered bitmap, the same proven pattern the per-deck visuals use. The audio itself was never broken — a test recording of the master bus proved it loud and clean; only the picture was frozen.
- **The DJ timer is now fault-isolated.** Each 100 ms step (deck refresh, cruise, sampler, mic, mixer, companion, set record) runs guarded: if one ever throws, it's written to crash.log once and the remaining steps still run — one bad step can never silently freeze the visuals downstream of it again.

## Version 3.85 — Spectrum: Case Closed
- **The master spectrum works — three stacked bugs, all fixed.** A diagnostic probe built into the DJ board caught each layer red-handed: (1) the strip's Image measured **0×0 forever** (no sized source at startup), and the draw code read its size *from the Image* and skipped every frame under 10px — a deadlock that meant no frame ever landed; the drawing is now sized from the strip's border. (2) The frame was presented through a DrawingImage captured once at startup, which freezes on frame one (fixed in 3.83 with per-tick bitmap rendering). (3) Even with a frame landing, the raw FFT band values run in the *thousandths* — drawn raw, the bars were a quarter-pixel tall. The strip now uses the same auto-gain scaling as the deck visuals, so the bars dance at any volume.
- Verified live on a playing deck: the strip animates frame to frame, the channel VU meters fill with the music, the deck spectrums and cassette reels move as they should. The audio itself was never broken — a test recording of the master bus along the way proved it loud and clean; only the pictures were lying.

## Version 3.83 — The Master Spectrum Lives Again
- **Fixed: the master spectrum strip was a permanently black bar.** It drew every tick into a DrawingVisual, but the strip's Image held a DrawingImage captured ONCE at startup over the visual's first Drawing — and every RenderOpen *replaces* that Drawing, so the strip displayed the first (silent) frame forever while the music played. It now presents each frame as a rendered bitmap, the same proven pattern the per-deck visuals use. The audio itself was never broken — a test recording of the master bus proved it loud and clean; only the picture was frozen.
- **The DJ timer is now fault-isolated.** Each 100 ms step (deck refresh, cruise, sampler, mic, mixer, companion, set record) runs guarded: if one ever throws, it's written to crash.log once and the remaining steps still run — one bad step can never silently freeze the visuals downstream of it again.

## Version 3.82 — The App's Own Music Library
- **Fixed: songs added from search never showed up in the track list.** The Search Hub's Add buttons (Archive, Audius, Jamendo, Pixabay) were filing full songs into the *sound* library (the sampler's clips), so they never appeared in the 🎵 Song Library — and couldn't be sent to decks or crates from there. They now add to the Song Library properly, and the status line says so.
- **BeatSync now keeps its own music library folder.** Every song you add — from search, Add Files, Add Folder, ZIPs — lands as a file in ONE library folder, and the Song Library's track list is the list of everything in it. The folder shows at the top of the 🎵 Song Library (📁 Local Songs); **Change…** picks a different one (default: Music\BeatSync Library), **📂 Open** opens it in Explorer.
- **Imports drop into the folder.** "Copy imports into the library folder" (on by default): Add Folder / Add Files copy the songs into the library folder so the library is self-contained; ZIPs unpack there too. Untick it to list songs where they already live instead. Adding the same song twice never makes a duplicate file or a duplicate list entry.
- **Delete, with a confirmation — always.** 🗑 **Delete File…** removes selected songs from the library and deletes their files from the library folder — but only after the app shows exactly what will happen and you confirm. Songs whose files live outside the library folder are only removed from the list (their files are never touched), and the confirm dialog says so. The old 🗑 Remove (list-only, files never deleted) is still there.

## Version 3.81 — Mixkit: The Honest Answer
- **Mixkit gets a tab — an explainer, not a scraper.** Checked against primary sources (Oct 22, 2026): Mixkit has **no API at all** (its /api and /developers addresses are 404; there is no api.mixkit.co), and its User Terms expressly forbid scripts or bots accessing or downloading its catalog. A search panel would have to scrape the site — prohibited — so BeatSync doesn't build one. Same standard as Free Music Archive.
- The 🎚 **Mixkit** tab (and Tools → 🎚 Mixkit) says all that plainly and offers the legitimate one-click-longer route: **open Mixkit's free music in your browser**, download there like any visitor, then import the files through the Song Library / Sound Library importers you already have.
- If Mixkit ever ships a public API, this tab becomes a real search panel.

## Version 3.80 — Pixabay Joins the Search Hub
- **A fifth source:** 🎼 **Pixabay** — royalty-free music under the Pixabay Content License (free to use, no credit required) — is wired into 🔎 Search Sounds & Music like the others: its own tab, a scope in the picker, "All sources" / "All songs" coverage, and a Tools → 🎼 Pixabay Music shortcut.
- **Your free key, stored like the others:** Pixabay's audio API needs a free API key (shown on pixabay.com/api/docs when you're signed in). Paste it into 🔑 Accounts & Keys — it's stored encrypted on this PC, sent only to pixabay.com, and the panel stays honest before it's saved, exactly like Jamendo.
- One transparency note: Pixabay doesn't publish the audio API's response format in its public docs, so the panel reads the reply defensively across the field names the Pixabay API family uses — and if a reply ever arrives in an unrecognized shape, the status line says so plainly instead of inventing results.

## Version 3.79 — Audius Joins the Search Hub
- **A fourth music source, zero setup:** 🎧 **Audius** — the artist-run streaming co-op — is now a full citizen of 🔎 Search Sounds & Music: its own tab, a scope in the search picker, and part of "All sources" and "All songs". Audius's public API asks for no key and no account (just an app name), so it works the moment you open the window — the 🔑 Accounts & Keys tab says so, correcting the old "coming soon" note.
- **Preview streams, downloads where artists allow them:** ▶ Preview streams the track (the app resolves Audius's stream redirect first so previews actually play). ⬇ Add to Library pulls the artist's own download in its original format where the artist enabled downloads — the results list marks those with ⬇ — and a stream copy otherwise; either way it's filed into your library, categorized, with the artist credited in the status line.
- Tools menu gains **🎧 Audius Music...**, opening the hub right on the Audius tab.

## Version 3.78 — Set Builder (Crates)
- **Plan the set before you play it:** Tools → **🧺 Set Builder** opens the crate workshop. Create named crates (Friday Warm-Up, Parade Set, whatever), then click songs in your Song Library to add them — filter box up top, double-click adds too, duplicates are refused politely.
- **Shape the order:** Up/Down buttons, Remove, and a **📈 Sort by BPM** that ramps the crate slowest → fastest for an energy build (songs you haven't analyzed yet go last, with a nudge to analyze them in the Library). The header always shows the running total: song count, full set length, and BPM range.
- **Perform it in one click:** **▶ Queue Whole Crate on Deck A** (or B) drops the entire crate, in order, into that deck's track list — then Auto-play or Cruise performs your set while Record My Set captures it.
- Crates are saved on this PC (they survive restarts), and each song keeps the BPM/key/length it had when you added it.

## Version 3.77 — Record My Set
- **One click, whole set:** the new **🎙 Record My Set** button in the mixer starts recording instantly — no file dialog, no naming to think about mid-performance. The button counts up while you play (⏹ Stop Set (12:34)).
- **It lands in Music\BeatSync Sets**, auto-named with the date and time (`Set 2026-10-22 8-45 PM.wav`).
- **The tracklist writes itself:** every time a deck starts a new song, it's logged with its timestamp and deck. When you stop, a matching .txt sits beside the WAV — date, duration, track count, and the full timestamped tracklist, ready to paste anywhere you post the set.
- It plays fair with the other recorder users: a running mashup capture or Record Mix gets a clear "stop that first" message instead of a hijack, and closing the DJ board mid-set finishes and saves the set properly.

## Version 3.76 — Sampler Pads Can Stop Now
- **Hit a playing pad again and it stops.** One-shot pads used to be fire-and-forget — a long loop or riser, once triggered, played to the end no matter what. Now pads toggle: playing → hit it again → silence, with the pad's lit flash clearing itself. The pad right-click menu gains a **⏹ Stop** item too, and the pad tooltips say how it works.
- The other stop paths were audited and confirmed working in this pass: the sound picker's Preview ⇄ Stop toggle (previews run through the sampler bus, stop cuts them dead), the main library's Preview ⇄ Stop, and **⏹ Stop Both Decks** in the mixer for mashup playback — it stops and rewinds both decks and ends Cruise if it's running.

## Version 3.75 — MIDI Controller Support
- **Real hardware on the DJ board:** plug in any USB MIDI controller and drive the decks with it — the new **🎹 MIDI** button in the DJ board's top bar opens the setup window.
- **What it drives:** Deck A/B Play-Pause and Cue, the crossfader, both channel faders, and all 8 sampler pads.
- **Learn for everything:** every control has a **Learn** button — press it, touch the button / pad / fader on your controller, and it's bound (channel included), saved on this PC for good. Reset returns any row to its default. Sensible defaults ship in the box (notes 36–47, CC 31 crossfader, CC 17/18 faders), so many controllers do something useful before you teach it anything.
- **Live monitor:** the setup window shows every message your controller sends, and the board stays live while it's open — move a fader on the hardware, watch the crossfader move on screen.

## Version 3.74 — Autosave + Crash Recovery
- **Your work now saves itself:** every 2 minutes, if the project actually changed, BeatSync quietly writes an autosave — a real .bsp project file, written by the exact same code as Save As. No popups, no interruptions, and it skips the write when nothing changed so big projects don't churn the disk.
- **Crash recovery:** if the app (or the PC) goes down mid-session, the next launch knows — and offers the autosave back with one click: "Restore it?" Your normally-saved projects are never touched. Closing the app properly clears the autosave away, so the offer only ever appears after a genuinely bad exit.
- **File → ♻️ Recover Autosaved Project:** the autosave is also reachable by hand any time one exists.

## Version 3.73 — Your Suno Songs, On the Decks (Streamed, Never Downloaded)
- **New ☀️ Suno tab in the Song Library:** press **🔄 Sync My Suno Songs** and the library reads your Suno library from the built-in browser (sign in once in the browser strip at the bottom — it's the same sign-in the My Songs window uses). It finds your songs, then checks your public profile to mark each one **Published** or **Draft**. Covers, titles and status all land in the grid, and the list is kept on this PC between sessions.
- **⬇ Stream to Deck A / B:** a Suno song loads straight onto a DJ deck by *streaming* — the exact same audio stream Suno's own web player plays, fetched into memory. It never uses Suno's Download button and never touches your download quota. Double-click does it too, and 🔍 Analyze reads BPM + key off the stream so you can plan the mix first.
- **🎧 Spotify tab:** the honest state of play, spelled out in the app — Spotify's audio can't run through any DJ mixer (platform rule), but playlist *track lists* can be imported for set planning once you've made a free Spotify developer app (five minutes, steps in the tab). Say the word when you've got the keys and the import gets built in.
- Nothing is baked in: the Suno tab shows whoever is signed in — your account, your songs, including the unpublished ones.

## Version 3.72 — The Song Library: Your Songs, Sorted by Their Tags
- **New 🎵 Song Library (Tools menu):** a proper crate for full songs, kept separate from the short-sounds library like you asked. Add a **folder** (it scans everything inside), a **ZIP** (the songs are pulled out and kept), or individual **files** — MP3, FLAC, WAV, OGG, M4A and more.
- **ID tags read automatically:** title, artist, album, genre, year, and any BPM / key already tagged in the file — shown in a grid that **sorts by any column** (click a header: sort by BPM to line up a remix, by key, by artist…). A filter box narrows the list as you type, and picking a song shows its **cover art** and full tag details on the right.
- **🔍 Analyze BPM + Key:** for songs whose tags don't carry them, one click runs the same BPM and key detectors the DJ decks use and fills them in — so the whole crate is remix-ready.
- **Straight onto the decks:** ⬇ Load on Deck A / B (double-click a song works too), or ➕ add songs to a deck's track list without loading them. The DJ board opens automatically if it isn't open. The library remembers everything between sessions, and removing a song from the list never deletes the file.
- **Small fix:** the gold version chip on the main window now reads the app's real version — it had been stuck showing v3.65.

## Version 3.71 — One Search Box for Every Source + Accounts & Keys
- **One search box (🔎 Search Sounds & Music):** the search window now opens with a single search box and a scope picker — 🌐 All sources, 🔊 Sounds (Freesound), 🎵 Songs (Archive), 🎵 Songs (Jamendo), or 🎵 All songs. One search hunts everything you picked at once; the counts land in a summary line ("Freesound: 12 · Archive: 30 · Jamendo: 8") and the window jumps to the first tab with results. Each tab keeps its own results list, preview and Add to Library — the tabs' own search rows are retired, since the one box drives them all.
- **🔑 Accounts & Keys tab:** the key tab is renamed and reorganized into plain sections — one per source — each saying exactly what it needs and what it costs: Internet Archive needs nothing (FREE, always ready), Freesound needs a free account + free API key, Jamendo needs a free developer account + free client ID. Each shows a live ✅ Connected / ⚠️ Not connected status. A "Coming in future updates" section notes Audius and Pixabay (both free keys) and is honest about the two that can't ever connect: Mixkit has no API, and Free Music Archive shut its API down.
- **Menus sorted out:** the View menu was carrying a toolbox — it now holds just Mixer, Output Window and DJ Decks (things you *view*). Everything else moved to a new **Tools** menu: the unified search first, the three source shortcuts (they still open the hub on their tab), then Patterns, Piano Roll, Drum Generator, Monitor FX, Transcript, Stem Splitter, AI Lab, My Songs, Sound Pack and Create Song. Nothing was removed — everything just lives where it belongs.

## Version 3.70 — Shuffle + Repeat on Every Deck
- **🔀 Shuffle (per deck):** each deck's track list gains a Shuffle toggle. With Auto-play on, when a song ends the deck jumps to a random *other* song from that deck's own track list instead of the next one in order — each deck shuffles independently.
- **🔁 Repeat track (per deck):** a Repeat toggle beside it. When the song ends, the same track starts over from the top. Repeat works on its own (no need for Auto-play) and wins over advancing — turn it off to let the list move on.
- Both toggles sit right under the "Auto-play the next track" checkbox in each deck's Track List section, with hover help explaining exactly what they do.

## Version 3.69 — The Mic Section Is Back (Mixer Column Scrolls)
- **Bug fix:** the center mixer column had quietly outgrown its fixed space as new buttons were added over the last several updates (Mashup, Save Mashup, Stop Both Decks, the stems picker). Everything at the bottom of the column — the whole 🎤 MIC block: mic picker, MIC on/off, level meter and knob, "Duck music while talking", Mic FX and the Companion button — was being clipped off the bottom of the board. Nothing was deleted; it was pushed off-screen.
- **The column scrolls now:** the mixer column has its own scrollbar, so the mic and ducking controls are always reachable — and no future button added to this column can ever push anything off the board again. Scroll with the mouse wheel anywhere over the mixer (wheels over knobs still turn the knobs, as before).

## Version 3.68 — Per-Deck Auto-Gain
- **No more volume surprises between songs:** each deck now measures a track's loudness the moment it loads and trims the deck so every song plays at the same level (RMS matched to a standard target, peak-capped so a trim-up can never clip). Quiet songs come up, loud songs come down — before the crossfader, so mixes sit together properly.
- **Per-deck toggle:** the "Auto-Gain: ON/OFF" button sits next to each deck's Gain knob, with the live trim shown beside it (e.g. "auto -3.5 dB"). Your Gain knob still works exactly as before, trimming on top of the auto level. Turn it off and tracks play at their recorded level.

## Version 3.67 — Suggest Next Song
- **New 🎯 Suggest button on each DJ deck:** load a track, hit Suggest, and BeatSync ranks your library songs by how well they mix *after* that deck's track — Camelot key compatibility counts most (same key, relative major/minor, and ±1 energy shifts score highest), BPM closeness second, with half/double-time tempos folded in fairly. The ranked list shows each song's key, BPM, and a match %.
- **Pick loads the other deck:** double-click a suggestion (or hit "⬇ Load onto Deck…") and it loads straight onto the opposite deck, ready to mix in.
- **It learns your library:** key and BPM analysis results are cached (the key cache you already had, plus a new BPM cache), so the first Suggest run over new songs is the slow one and every run after is faster. Split stems and short hits/loops under 45 seconds are never suggested as a "next song."

## Version 3.66 — Jamendo Joins the Search Hub
- **New music source:** the 🔎 Search Sounds & Music window gains a third source — **🎵 More Music — Jamendo**. Search jamendo.com's library of free, Creative Commons-licensed full songs by independent artists, preview them streaming, and add them to your library (filed into categories automatically, with the license and artist shown for credit) — same flow as the Archive tab.
- **Free client ID, your account:** Jamendo needs a free client ID from devportal.jamendo.com (sign up, create an app, copy the ID). Paste it in the hub's renamed **🔑 API Keys** tab, which now holds both the Freesound key and the Jamendo ID — stored encrypted on this PC, sent only to Jamendo. The tab has a one-click button that opens the signup page.
- **View menu:** a new "🎵 Jamendo Music..." entry opens the hub straight on the Jamendo tab.

## Version 3.65 — Audit Fix 6: Warning Residue Cleared
- **Cleanup (audit #6 of 6 — the audit list is now fully worked through):** removed a never-read variable from the download engine, and untangled a drag-and-drop helper that aliased its own parameter into a same-named local and nulled it (the compiler's last real warnings). The build now compiles with only one warning left — a deliberate one on the playback dispatch, which is fire-and-forget by design and guarded inside.

## Version 3.64 — Audit Fix 5: Create Song Slider Cleanup
- **Cleanup (audit #5 of 6):** the Create Song window's constructor used to "sync the slider labels" by calling the slider event handler with a fake null argument — it worked only by accident. The label sync is now its own little method that both the handler and the constructor call properly. No behavior change.

## Version 3.63 — Audit Fix 4: Online Stem Split Hardening
- **Bug fix (audit #4 of 6):** the online (cloud) stem-split path trusted its inputs too much — it dereferenced the clip and the cancel token through null-forgiving shortcuts, so opening it with no file and no clip (or a clip with no audio) could crash instead of explaining itself. It now checks first ("Nothing to split — pick a sound file or a timeline clip first") and carries one captured cancel token through the whole run.

## Version 3.62 — Audit Fix 3: Beat Detection Can't Crash the App
- **Bug fix (audit #3 of 6):** running Detect Beats on a corrupt or unusual clip could crash the whole app — the two entry points that feed it are fire-and-forget (`async void`), where an unhandled error has nowhere to go but down. Both are guarded now: a bad clip simply reports "no steady beat found" (or tells you what went wrong) and the app carries on.

## Version 3.61 — Audit Fix 2: Sound-Library Archive Locking
- **Bug fix (audit #2 of 6):** the sound-library archive (`sound-library.zip`) was opened from several places at once with no synchronization — the screen, imports, mashup saves, and the background "stems are ready" callback could all hit it simultaneously, causing "file in use" errors and risking a torn archive (the likely cause of the unreadable-archive incident on Oct 22). Every archive read/write now goes through a single lock, so those operations line up instead of colliding. No visible behavior change — just no more collisions.

## Version 3.60 — Audit Fix 1: OGG / FLAC / M4A Now Actually Play
- **Bug fix (2026-10-22 full-code audit, #1 of 6):** OGG, FLAC, M4A and WMA files imported fine but silently never played on the timeline side — nine decode spots treated every non-WAV file as an MP3 and the failure was swallowed. Playback, waveforms, clip durations, beat detection, stem splitting, transcripts, batch convert and exports all went through the broken path (the DJ decks and sampler already handled these formats correctly).
- All nine spots now share one **AudioReaderFactory**: WAV/MP3 read from memory as before; everything else decodes through Media Foundation, exactly like the decks do. If you have OGG/FLAC clips sitting silent in a project, they should just work now.
- MIDI files remain the one unsupported format (they need a synthesizer, not a decoder) — noted in the audit report.

## Version 3.59 — One Search Window: Sounds & Music Together
- **Freesound and the Internet Archive now live in ONE window** — 🔎 Search Sounds & Music — so all audio hunting happens in the same spot (Michael's ask).
- **Categorized tabs make it obvious what lives where:** 🔊 **Sounds & Samples** (Freesound: drum hits, loops, effects, ambience) · 🎵 **Music & Recordings** (Internet Archive: full songs, live concerts, albums, old radio) · 🔑 **Freesound Key**. A plain-language line at the top of the window and inside each tab spells out which is which.
- Both old menu entries (🌐 Freesound Sounds…, 🏛️ Internet Archive Music…) and the command palette now open this window on the matching tab. The two separate search windows are retired; nothing else about search, preview, licenses, or Add to Library changed.

## Version 3.58 — Internet Archive Music Source
- **New 🏛️ Internet Archive window** (Create menu, next to Freesound): search the Internet Archive's enormous free audio library from inside the app — and unlike Freesound it needs **no account and no API key, it just works**.
- **Scope picker:** All audio, 🎤 Live Music (the Live Music Archive's concert recordings), 💿 Netlabels (free online record labels), or 🌐 Community Audio.
- Archive items are albums and shows, so the window pulls the **individual MP3 tracks** out of the top matches (up to 48), each row showing the track, its length, its **license**, and its creator so credit is one copy-paste away.
- **▶ Preview streams the track** straight from archive.org; **⬇ Add to Library** downloads it through the usual Downloads tracker and files it into a library category automatically, ready for the timeline, the decks, and the sampler.
- Also listed in the 🔑 Providers window alongside Freesound.

## Version 3.57 — DJ Key Lock
- **New per-deck 🔒 Key Lock toggle** (in the Key row, next to Key Sync). With it ON, moving the tempo fader changes the deck's **speed only — the pitch stays locked** at the track's key, like Serato/Rekordbox master tempo. With it OFF, the deck stays vinyl-style and pitch rides the tempo like it always has.
- The lock is live: nudge the tempo and the pitch compensation re-renders itself a beat later (a full-song render takes a second or two on the Dell, so the pitch glides back into lock right after you move the fader — same feel as the existing Key Sync). SYNC and the tempo slider both respect it.
- **Key Lock and Key Sync compose:** sync a deck to the other deck's key AND lock it, and the tempo fader will hold that synced key. The key label and the Camelot compatibility coloring now show the key you're actually hearing — with the lock on, tempo no longer drags the displayed key around.
- Toggling Key Lock OFF restores honest vinyl behavior instantly (original buffer, no shift); if Key Sync was also on, turning sync off while locked keeps the lock's compensation.
- Note: the lock applies to the full mix (and to stems that were already split when you toggle it), same as Key Sync — stems split afterwards join at their natural pitch.

## Version 3.56 — MilkDrop Visuals (projectM)
- **The Output window's Visuals get the real MilkDrop engine.** Pick **🌀 projectM (MilkDrop)** in the Scene picker and the stage runs actual MilkDrop presets — the swirling, beat-reactive visuals from the Winamp days — rendered live by libprojectM and fed from the master audio, so what you hear is what it dances to.
- **Four starter presets ship with the app** (written for BeatSync, since the engine ships with none): **Green Pulse**, **Gold Tunnel**, **Purple Waves** and **Vermont Pines**. They rotate on their own every 30 seconds; the ⏮ / ⏭ buttons in the Output bar skip between them and the bar shows the current preset's name.
- **Bring your own presets:** the **📁 .milk…** button in the Output bar adds any folder of MilkDrop (.milk) presets to the rotation — thousands of community presets from the Winamp era work as-is.
- Auto Cycle now includes the projectM scene when it's available. If the engine ever can't start on a machine, the Output window quietly falls back to the built-in scenes instead of breaking.
- Under the hood: the engine is a separate, replaceable library (LGPL) that sits beside the program rather than inside it, running on its own render thread at 30 fps and capped at 720p internally so it stays light on the graphics chip, then upscaled to the stage.

## Version 3.55 — Stems, Nested
- **The stems sub-categories now nest under one Stems parent** (Michael's layout): instead of "Stems · Bass", "Stems · Drums" and friends sitting in the category list as flat rows, there's a single **+ Stems** row with the total count, and the sub-categories live underneath it, indented: **-- Bass**, **-- Drums**, **-- Other**, **-- Vocals**. Click the Stems row to fold them away or bring them back.
- Picking **Stems** itself shows every stem at once; picking a sub-category shows just that kind.
- The DJ sampler's sound picker uses the same nested layout, so both browsers work the same way.

## Version 3.54 — Under-the-Hood Tune-Up
- **The DJ board audit is now fully worked through** — this build clears the last batch of small findings:
- Knobs redraw properly when their range changes and can't get stuck in a feedback loop.
- Typing a BPM the box can't use (letters, or outside 40–240) now tells you and restores the real value, instead of silently keeping the old BPM.
- The DJ board's sliders answer clicks on their tracks, not just drags.
- The two decks share the saved-key file safely now — one deck can no longer wipe the other's detected key.
- The mic channel disconnects cleanly, and old stem-split jobs are properly disposed of.
- The little deck visual screens reuse their brushes and bitmap instead of rebuilding them ten times a second — smoother and lighter on memory.

## Version 3.53 — ID Tags & Cover Art
- **The app now reads the tags inside your MP3 and FLAC files** — title, artist, album, year, genre, the BPM and key written in the file, and the embedded cover art. No new downloads or libraries; the reader is built in.
- **On the DJ decks:** load a tagged song and the deck header shows the cover art thumbnail and the tagged "Artist – Title" instead of the bare file name, with an album • year • genre line under it. The new **🏷️ Tags** button on each deck opens the full tag sheet with the cover art big.
- **While browsing:** in the sampler's sound picker, selecting a tagged MP3 or FLAC shows its cover thumbnail and tag summary at the bottom of the window — so you can see what a file really is before you use it.

## Version 3.52 — Every Window, Same Dark Look
- **The white-on-white controls are fixed everywhere at once.** The Stem Splitter's Cancel button, the Create Song window's Cancel button and sliders, the Freesound key box and buttons — they all had the same root cause: those windows never styled their plain buttons, text boxes and sliders, so Windows rendered them with the stock light theme on a dark window. Instead of patching window after window, the app now carries one shared dark style for buttons, text boxes, password boxes, sliders, list boxes, tabs, menus, tooltips and progress bars — every window picks it up automatically, including ones added later.
- Windows that already had their own look keep it: the main window's buttons, the AI Lab and the DJ board's hardware controls are unchanged.
- Sliders everywhere are now dark with a green filled track, and buttons highlight on hover and grey out properly when disabled (like the Stem Splitter's Cancel does before a file is picked).

## Version 3.51 — Sidebar Cleanup
- **The library search box is gone** (Michael's call — it sat unlabeled under the Import button and anything typed into it silently filtered the sound list to empty). The category list + sound list are the way to browse now, exactly like the DJ sampler picker.
- **Buttons renamed:** Quick Video → **Add Video**, Quick Images → **Add Image**, Quick Audio → **Add Song**.

## Version 3.50 — Timeline Zoom, Roomier Timeline, Stems Sub-Categories Fixed
- **Mouse-wheel timeline zoom:** roll the wheel over the timeline to zoom in and out (10%–1000%), anchored at the cursor so the moment under the mouse stays put. The ruler adapts as you zoom: whole seconds when zoomed out, splitting into halves, quarters and tenths of a second when zoomed in (1.25s, 1.5s, 1.75s…), with longer ticks on the whole seconds. A zoom readout sits in the timeline header. Clips, beat grid, loop region, playhead, playback, export and transcript timing all follow the zoom; project files still save at the standard scale, so zooming never corrupts a saved project.
- **The video preview box hides itself:** when no video clip is on the timeline, the big empty player above the timeline goes away and the timeline takes the full workspace height. Add a video clip and the player comes right back.
- **FIX — stems really are sub-categorized now:** a one-character parsing bug clipped the stem type out of cached file names ("(bass)" was read as "bas"), so every stem quietly fell back into the one flat "Stems" pile in the sidebar and the DJ picker. Fixed in both places, with a second-chance refine pass behind it: stems now file under Stems · Bass / Stems · Drums / Stems · Other / Stems · Vocals, right below Stems, and their names read in full ("… — Bass", not "… — Bas").

## Version 3.49 — Menu Readability Fixes
- **FIX — AI Lab dropdown text was black-on-dark:** the selected value in every closed dropdown (Provider, Model, Make, video model, Keys & Compute pickers) rendered in the stock black because the dropdown's inner button broke the text-color chain. The selection text now inherits the app's white properly.
- **FIX — timeline clip menu could open half-clipped:** the clip right-click menu is now anchored to the clip and opens underneath it, fully inside the window, and its volume/speed slider rows are a bit narrower so the whole menu fits comfortably.

## Version 3.48 — Stems Get Their Own Sub-Categories Everywhere
- **FIX — dropdowns were white-on-white too:** every ComboBox in the app (AI Lab model pickers, DJ board, everywhere) now uses one proper dark dropdown style — dark box, white text, dark popup list with a green selected row.
- **FIX — timeline clip right-click menu was white-on-white:** the clip menu popup is now properly dark with light text throughout (items and the volume/speed slider labels), and the library's right-click menu got the same treatment.
- **Preview is one toggle button now** (DJ sampler picker + editor library): press ▶ Preview and it becomes ⏹ Stop while the sound plays; press it again to cut the sound, or it flips back to Preview on its own when the sound ends. (Preview itself now runs through the sampler engine — the same output as the pads — instead of a separate player that could stay silent.)
- **Stems are sorted by type, no exceptions:** any sound sitting in the plain "Stems" bucket — older splits, stems imported from a ZIP, stems saved in a project before sub-categories existed — is now filed by what it actually is: **Stems · Vocals / Stems · Drums / Stems · Bass / Stems · Other**, in both the editor library and the DJ sampler picker. The type is read from the file name ("(vocals)" suffixes, bare stem names, "Song — Vocals" tails), so nothing needs re-splitting.

## Version 3.47 — One Archive for Sounds, Right-Click Tools, Download Title
- **FIX — the main-screen list could come up empty after importing:** the sidebar browser now reads the SAME sources as the DJ sampler picker (built-in pack, your sound-library archive, stem cache, Downloads), so every sound the picker can see also shows on the main screen. Picking an outside sound pulls it into the project library on the spot.
- **NEW — imported sounds live in ONE compressed file:** everything you import is stored in a single `sound-library.zip` (entries sorted by category) instead of a pile of loose files — less disk clutter, and the whole app (editor library, DJ picker, sampler pads) reads straight from it. Any loose files from the old folder layout are packed in automatically and the folder is cleaned up.
- **NEW — right-click any sound (editor library AND the DJ picker):** 📤 Export… (save a copy anywhere), ✏️ Rename… (renames it in the project and in the archive), 🗑️ Delete… (with the usual confirm).
- **Downloads moved to the File menu** (the little ⬇ button by the search box is gone), and **while a download is running, the window's title bar shows it live**: file name, percent, speed and ETA.
- Sampler picker window is wider, so its button row no longer cuts off the Cancel button.

## Version 3.46 — Grouped Library, ZIP Import, Saveable Mashups
- **NEW — the main Asset Library is now a browser, just like the DJ sampler picker:** a category list on the left side of the editor (All + Drums, Guitar, Bass, Keys & Pads, Loops, Vocals, FX, Stems · Vocals/Drums/Bass/Other, 🔀 Mash-ups, Other, Video, Images — each with a count) and that category's sounds listed below it. Click a sound to arm it for the timeline, drag it straight onto a track, double-click (or ▶ Preview) to hear it first, ⏹ Stop cuts a long preview, and search filters the list. The old scrolling button-list library is gone.
- **NEW — 📦 Import ZIP / files… (library sidebar + the sampler picker):** import whole ZIP sound packs or loose WAV/MP3 files; every sound is auto-categorized (pack folder names first, then name keywords), filed into your sound-library folder, and **duplicates are never added twice** (same name + same size = skipped; same name but a different sound is kept, numbered).
- **NEW — delete sounds individually, with a confirm:** right-click any library sound → 🗑️ Delete sound…, or the picker's Delete button for library/imported sounds. Nothing deletes without the confirm dialog.
- **NEW — 💾 Save Mashup:** every 🎤 MASHUP is captured automatically while it plays; if it's a keeper, one press saves it (silence trimmed off both ends) into the library under a new **🔀 Mash-ups** category, ready to drag onto the timeline. **MIX A+B and 🎲 Best Pair are removed** (Michael's call) — Mashup is the one-button mix now.
- **NEW — ⏹ Stop Both Decks button:** stops both decks at once and rewinds them to their cue points (a playing mashup's capture keeps what already played, so it can still be saved).
- **NEW — "Stems run on" picker in the DJ mixer:** Auto / GPU / CPU for stem splitting. Auto uses the graphics card when the PC has a proper one (DirectML) and the CPU otherwise; GPU/CPU force the choice. Applies to Mashup and Split Stems alike.
- **Sampler pads wear their sound's name:** assign a sound and the pad's face takes that sound's name (and keeps it next launch); clearing a pad resets its face.
- **Stems are filed by type now:** a split no longer dumps all four stems into one pile — the library and the picker get separate **Stems · Vocals / Stems · Drums / Stems · Bass / Stems · Other** groups.
- **Picker fixes:** ▶ Preview now plays through the sampler engine itself (the old preview could stay silent) with a ⏹ Stop that cuts long sounds dead, and the ✓ Use button no longer renders as a blank white box when disabled.

## Version 3.45 — Sampler Sound Picker Window
- **NEW — assigning a sampler pad opens a real sound browser window** instead of a bare file dialog: every sound sorted into categories — the built-in pack (read straight out of the compressed soundpack.zip), your editor library (including split stems), stem files on disk, and the PC's Downloads folder — each category with its sounds listed alphabetically.
- **Preview before you commit:** select a sound, hit ▶ Preview to hear it, then ✓ Use this sound (or double-click). A 📁 Browse files… button still lets you grab any file from anywhere.
- Pad assignments from the pack or library remember *where* the sound lives (not just a file path), so they reload correctly next launch.

## Version 3.44 — Stems Land in the Library
- **NEW — 🧬 Stems category in the editor's asset library:** whenever a stem split finishes — in the editor's stem splitter OR on a DJ deck — the four stems (vocals / drums / bass / other) are automatically filed in the main window's Asset Library under a new **Stems** category, named like "Song — Vocals", ready to drag onto the timeline like any other sound. Cached stems loaded on a deck are filed too, and re-splitting the same song doesn't create duplicates.

## Version 3.43 — 🎤 MASHUP A+B + Progress Windows
- **Why mixes sounded bad, fixed properly:** two FULL songs at once is always lyrics-over-lyrics. New **🎤 Mashup** button (mixer, next to MIX A+B): Deck A plays **vocals only**, Deck B plays **the beat only** (drums + bass + other), tempo- and key-synced and started together — an actual remix. If a song's stems aren't split yet, it splits them first (with the usual model download prompt the first time ever) and everything is cached for next time.
- **Progress windows:** 🎲 Best Pair and 🎤 Mashup now open a live progress window — a progress bar plus a step-by-step log (each song's BPM and key as it's analyzed, the winning pair, stem splits, and the final setup) instead of just the little status line.

## Version 3.42 — Roomier Mixer, 🎲 Best Pair, Clickable Spectrums
- **Mixer layout fixed:** the center column is wider (352) and the whole lower section (crossfader/curve/fade, cruise, mic, companion) is stacked full-width — the mic picker, **Duck music while talking**, and the **Mic FX / Companion** buttons are no longer clipped or cut off. Knobs and faders got a small trim so everything fits without scrolling.
- **NEW — 🎲 Best Pair** (next to MIX A+B): pick a bunch of songs (up to 12) and the app analyzes each one's BPM and key, scores every pair for tempo + Camelot-key compatibility, loads the best-matching two onto the decks, and runs MIX A+B on them — the pair that will actually remix well, not two random songs.
- **Clickable spectrums:** click any deck's visual to cycle its spectrum density (28 fat bass-heavy bars → 48 normal → 84 fine detail); the master spectrum in the top bar clicks the same way (32/64/96).

## Version 3.41 — 🔀 MIX A+B, Knob Reset, Honest SYNC
- **NEW — 🔀 MIX A+B button** (top of the mixer): load a track on each deck, press it once, and it sets the whole mix up for you — auto-detects any missing BPM (the same beat detector as the editor's beat grid), matches Deck B's tempo to Deck A, key-syncs Deck B to Deck A, centers the crossfader on the Constant Power curve, and starts **both decks together** from their cue points. The status line tells you exactly what it did.
- **Knob reset:** double-click any knob in the DJ window to snap it back to its default (gain 1.0, EQ flat, master/mic/sampler 0.9), and each channel strip has a **↺ Reset** button that resets the whole strip (gain + EQ + fader) at once.
- **FIX 10 — SYNC tells the truth now:** if two BPMs are too far apart for the ±8% tempo range, SYNC tries half/double-time matching and says so; if even that doesn't fit, it leaves the tempo alone and tells you, instead of silently clamping and claiming success.

## Version 3.40 — 📐 No-Scroll Board, Hover Help, Programmable Pads
- **No more scrolling, anywhere:** each deck is rearranged into two-column zones (platter + waveform on the left; visual, load, stems and transport on the right; tempo/BPM/key beside hot cues/loops/gain; beat jump and the track list full width), the mixer is compacted (crossfader + cruise beside mic + companion), and the whole board is designed at 1800×1000 and scales down to fit smaller screens. Every control is on screen at once — scrollbars are gone.
- **Hover help on everything:** sit your mouse on any knob, button, slider, pad or picker in the DJ window and a plain-English popup explains what it does (pops up fast, stays while you read).
- **Every sampler pad is programmable:** right-click any pad — including pad 1 — for **Assign sound…**, **Rename pad…** and **Clear sound**. Names are saved (sampler-pad-names.json) and a cleared pad stays cleared instead of its built-in sound sneaking back.

## Version 3.39 — DJ Reliability Fixes (GLM Audit, Part 5)
- **FIX 9 — Phone companion hardening:** sign-in tokens now expire after 12 hours and are all cleared when the server stops; the wrong-password lockout escalates (30s, 60s, 120s… up to 15 minutes) for repeat offenders; the companion settings are cached in memory instead of re-read from disk on every phone request; the lockout reply is no longer written while holding the auth lock; if the listener ever dies unexpectedly the companion window now reports it instead of pretending to run; and sampler pad commands from the phone are bounds-checked before they touch anything.

## Version 3.38 — DJ Reliability Fixes (GLM Audit, Part 4)
- **FIX 7 — Cruise no longer freezes the app:** auto-DJ track loads (file read + decode) now run off the UI thread and are applied when ready, with a generation guard so turning cruise off mid-load can't resurrect it. Broken files in the queue are skipped silently instead of popping a dialog mid-set. Manual loads behave exactly as before.
- **FIX 8 — Slip cancel:** turning Slip mode OFF while holding a scratch now cancels the pending slip jump — releasing the record just keeps playing from where you are instead of teleporting to where the track "would have been".

## Version 3.37 — 🎛️ The Concept Board Layout
- **The DJ window is rebuilt to the approved concept layout** — the floor plan this time, not just the paint: a brand strip across the top with a **live master spectrum**, decks left and right, a real center mixer, and the sampler as a full-width bar along the bottom.
- **NEW — Channel strips in the mixer:** CH A and CH B strips with a **gain knob**, **HI / MID / LOW EQ knobs** (wired to each deck's real EQ — the same one the FX popup edits) and a **channel fader**, with live **VU meter columns** between them. The strip gain, strip fader and each deck's own gain knob are three surfaces on the same volume and stay in sync.
- **Everything from before is still here** — platters that scratch, slip, stems, key sync + Camelot, hot cues, loops, beat jump + quantize, track lists, cruise, sampler pads, live mic + ducking, phone companion, Record Mix. Nothing was cut; it all moved into the board layout.
- Decks and mixer now scroll internally, so the sampler bar and brand strip stay pinned on screen at any window size.

## Version 3.36 — 🛠️ DJ Reliability Fixes (GLM Audit, Part 3)
- **FIXED — Recording can no longer kill the audio:** Writing the mix recording to disk now happens on a background writer thread. The audio path only queues a copy of each block — no disk I/O on the audio thread, and a disk failure (full drive, removed stick) quietly ends the recording instead of taking the whole output down mid-set.
- **FIXED — Waveform passes no longer stall playback:** Computing deck peaks and channel splits (for waveforms and stem splitting) used to hold the deck's audio lock for a whole-song pass, glitching the playing deck. They now snapshot the audio and compute lock-free.

## Version 3.35 — 🛠️ DJ Reliability Fixes (GLM Audit, Part 2)
- **FIXED — Companion request cap:** The phone remote now rejects request bodies over 64 KB (HTTP 413) before allocating anything — previously any device on the Wi-Fi could force a huge memory allocation just by declaring a giant Content-Length.
- **FIXED — Button icons:** The CRUISE and MIC buttons, the Mic FX and Phone Companion buttons, and the MIC header now show their actual icons instead of literal "\U..." text — the labels were written as C# escapes, which XAML shows as raw text.

## Version 3.34 — 🛠️ DJ Reliability Fixes (GLM Audit, Part 1)
- **FIXED — Silent DJ window after reopen:** Closing and reopening the DJ window no longer leaves it silent. The DJ audio chain now detaches from the main mixer on close, and the reopened window's chain swaps in on open.
- **FIXED — Corrupt stem crash:** A corrupt or half-written cached stem file can no longer crash the app. Stem loads are guarded at the deck and the window; failures report "cached stems failed to load" instead of taking the app down.
- **NEW — Crash safety net:** An unhandled UI error is written to crash.log in the app-data folder and the app stays alive instead of dying mid-set; background-task exceptions get the same logging.
- Fixes from the GLM 5.3 Flash code audit (OpenCode), implemented under brief and verified line-by-line before shipping. More audit fixes are queued.

## Version 3.33 — 🎛️ The Hardware Board Reskin
### New look, same machine
- **The DJ window now looks like a real board** — the splash art come to life: dark brushed-metal panels, machined **rotary knobs** with lit tick rings (master, mic level, sampler volume, per-deck gain — drag up/down or scroll to turn), **fader caps** on the crossfader and tempo sliders, a proper **toggle switch** for mic ducking, and backlit hardware buttons with metal edging everywhere.
- **The platter got richer vinyl**: more groove rings and a finer cut, same glowing rim and waveform ring. The deck visual display is a taller, board-style screen — and yes, **every visual style is still there** (all 11 + Off, same picker, same colors).
### The guarantee
- **Nothing was cut.** This version changes paint, not features: scratching works exactly as before (same platter, same slip mode), and every control — stems, key sync, cruise, sampler, mic, companion, queues, loops, jumps — is wired exactly where it always was. The knobs and switches are the same settings wearing better clothes.

## Version 3.32 — 📱 Phone Companion (Wi-Fi + Tailscale, Password Gate)
### New
- **Control the DJ decks from your phone** (final feature of the DJ mega-queue): the **📱 Phone Companion** button in the mixer opens the companion panel — start the built-in server and open the shown address on your phone for a full remote: both decks (play/pause, CUE, live title/key/time), crossfader, master, Cruise, the mic, and all 8 sampler pads.
- **Two doors in, same server**: your home **Wi-Fi** address at home, and your **Tailscale** address from anywhere in the world — the panel lists both, ready to type. (No app to install on the phone; it's a web remote.)
- **🔒 Password gate, exactly as specified**: set a password in the companion panel and *every* connection must pass it — no password, no control. Stored as a salted PBKDF2 hash (never plain text), five wrong tries locks an address out for 30 seconds, and changing the password signs every phone out.
- **Bluetooth is phase 2, honestly**: phone browsers only allow Bluetooth from secure (HTTPS) pages, and this remote is plain HTTP on your own networks — so the panel says so instead of faking it. The unblock is a Tailscale HTTPS address for this same page.

## Version 3.31 — 🎤 Live Mic Channel
### New
- **A real mic channel in the DJ mixer** (feature 7 of the DJ mega-queue): pick your input device, hit **🎤 MIC: LIVE**, and you're on the mix — voice runs through its own EQ (rumble cut + presence lift) and a touch of reverb, tuned for talk-over, and lands in **Record Mix** recordings too.
- **Auto-duck**: with "Duck music while talking" on (default), the music pulls itself down to about a third the moment you speak and eases back up when you stop — fast down, slow up, like a radio board. Your voice is never ducked.
- **Live level meter + mic level slider**, and a **🎛️ Mic FX** button opening the same FX editor the decks use, so the voice chain is fully tweakable. No microphone plugged in? The section says so politely instead of crashing. (Latency is announcement-grade — made for talking over the mix, not for singing along to yourself.)

## Version 3.30 — ⏭️ Beat Jump + Quantized Cues
### New
- **Beat Jump row on each deck** (feature 6 of the DJ mega-queue): **-16 / -8 / -4 / +4 / +8 / +16** buttons hop the playhead by whole beats at the deck's effective BPM — skip the long intro, jump back 16 to re-drop the chorus — without ever losing the beat. No BPM set? The deck tells you to type or TAP one first.
- **Quantize (on by default)**: hot cues and the CUE point now **snap to the nearest beat** when you set them, and beat jumps land exactly on the grid, so everything you trigger fires on the beat, every time. One click turns quantize off for raw, unsnapped placement.

## Version 3.29 — 🌀 Slip Mode
### New
- **Slip mode on each DJ deck** (feature 5 of the DJ mega-queue): a **🌀 Slip** toggle sits right next to the Scratch button. Turn it on, grab the record and scratch, juggle, hold it still — underneath, the song keeps playing in perfect time on a shadow clock.
- **Release lands you back in the song**: the moment you let go, playback resumes from exactly where the track *would have been* — no lost place, no spin-up ramp, the beat never broke. Slip is loop-aware (the shadow clock respects an active beat loop) and changes nothing when it's off: normal scratch behaves exactly as before.

## Version 3.28 — 🎵 Key Detection, Camelot Wheel & Key Sync
### New
- **Every deck detects its track's key** (feature 4 of the DJ mega-queue): a chromagram + key-profile analysis runs in the background on load, and the deck shows the key and its Camelot code — "A min - 8A". Results are cached per file, so a song's key is instant next time.
- **Harmonic mixing at a glance**: when both decks have keys, the key readouts light **green** when the two tracks mix harmonically (same Camelot, one step either way, or relative major/minor) and **amber** when they clash.
- **🎡 Camelot wheel window** on each deck: the full 24-key wheel with your key in green, the other deck's in purple, and every compatible key tinted — picking the next song becomes a visual decision.
- **🎵 Key Sync**: one click shifts this deck's pitch to the other deck's key *without changing tempo* (offline WSOLA pitch shift; stem buffers shift too when stems are loaded). Click again to restore the original, untouched audio. Effective key readouts account for the tempo slider as well, since tempo bends pitch.

## Version 3.27 — 🎛️ Sampler Pads
### New
- **Sampler strip in the DJ window** (feature 3 of the DJ mega-queue): 8 one-shot pads in a full-width strip under the decks, mixed into the DJ master — so pads are heard live **and captured in Record Mix**.
- **Loaded from the built-in sound pack**: Impact 💥, Riser 🚀, Downlifter 📉, Crash 🥁, Clap 👏, 808 Drop 🔊, Cowbell 🔔. **Pad 1 is reserved for your 🎙️ RAD-DAD! tag** — it stays empty until you assign a recording of it.
- **Your sounds, your pads**: right-click any pad to assign any WAV/MP3 (it previews on assignment, and the assignment is remembered between sessions). Retriggering a pad restarts it, pads play over each other, each pad flashes while it sounds, and the strip has its own volume slider.

## Version 3.26 — 🚢 Auto-DJ Cruise Mode
### New
- **Cruise mode in the DJ window** (feature 2 of the DJ mega-queue): hit **🚢 CRUISE** in the mixer column and BeatSync DJs for you. It builds one playlist from **Deck A's track list followed by Deck B's**, starts on Deck A, and works down the list hands-free.
- **Real transitions, not hard cuts**: when the playing track has a fade's worth of time left, the next song loads on the *other* deck, **SYNCs automatically when both BPMs are known** (and just crossfades when they're not — missing BPM never blocks), starts playing, and the crossfader animates across over your chosen fade length — **2 / 4 / 8 / 12 seconds**. The old deck stops when the fade lands.
- **You always outrank the machine**: loading a track, pressing PLAY or CUE on either deck turns cruise off instantly ("manual control"), turning cruise off mid-fade freezes everything exactly where it is, and cruise politely ends itself when the playlist runs out. Broken files in the list are skipped, not fatal.

## Version 3.25 — 🧬 Stem Decks
### New
- **Stem decks in the DJ window** (first of the DJ mega-queue Michael greenlit): every deck gets a **🧬 Split Stems** button. It runs the same HTDemucs AI the Stem Splitter uses (one-time ~304 MB model download, same prompt) and splits the loaded track into **Vocals / Drums / Bass / Other** — then the four toggle chips appear under the deck.
- **Stems play through the deck's own clock**: one shared position drives all four stems, so tempo, SYNC, beat loops and scratching stay perfectly in sync while you drop the vocals for an instrumental break or kill the drums under an acapella. Toggles ramp click-free (no pops), per deck independently.
- **Splits are cached per track**: stems are saved under your app data, keyed to the exact file, so loading the same song again brings its stems back instantly — no re-splitting, no waiting.

## Version 3.24 — 🏠 Remote GPU ×2: Both PCs in the Pool
### New
- **Two engine slots** (Michael: "I want to be able to use both PCs if possible — can we use both at the same time or separately?"). The 🏠 Remote GPU card in 🌐 Providers now takes **Engine 1 and Engine 2** addresses — one for the GTX 1660 desktop, one for the RTX 3050 laptop — each with its own 💾 Save and 🩺 Test. Create Song / AI Lab keep the same single "🏠 Remote GPU" provider pick; behind it, the app now carries both addresses.
- **How both-at-once works, honestly:** every generation job runs on ONE engine (a single song can't be split across two GPUs), but the app **probes the pool in order and routes each job to the first engine that answers** — one PC asleep or off, the other takes the job automatically, and the status line names the engine that's working ("🎛️ Engine 2 is on the job — Engine online — GeForce RTX 3050…"). Run the app on both machines and each routes its own jobs to whichever engine is free-awake; the engines themselves will report busy state in a later step so two heavy jobs can spread instead of queueing on one card.

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
