using System;

namespace BeatSyncStudioPro
{
    /// <summary>
    /// Shared knowledge Michael and Jarvis built together (Oct 2026), injected as
    /// system context into every AI Lab tab through the shared provider layer
    /// (AiProviders.cs) — no tab prompt is changed, so all 13 tabs pick it up at once.
    ///
    /// The pack lives HERE, compiled into the app — not a sidecar file, so it
    /// cannot be deleted, moved, or go missing, and it ships with the next build
    /// like any other change. To update it, edit this file and ship a build.
    ///
    /// Contains NO secrets by design: no passwords, keys, tokens, phone numbers,
    /// emails, or LAN IPs. Hostnames and roles only.
    /// </summary>
    internal static class AiKnowledge
    {
        public const string Header =
            "Standing instructions — knowledge Michael and Jarvis built together. " +
            "Follow these unless Michael's current request says otherwise.";

        public const string Pack = @"## Michael
- Michael DeCato, Splash Car Wash Rutland crew, Rad-Dad (Vermont country rap, boom-rap style).
- Family: wife Tenisha, sons Mike Jr. (6) and Hudson (baby).
- Working style: one thing at a time, in his order; nothing counts as done until proven on his screen; save every clip and every decision.

## Brand + music rules
- Style term: boom-rap (hip-hop, boom-rap, heavy 808 sub-bass, hard-hitting bass). Rad-Dad voice 70% unless he explicitly chooses otherwise; audio influence 65-70%.
- His lyrics verbatim, approval before generating. Never download from Suno without asking first (monthly quota).
- Artist RadDadVT802 only; hashtag #802 (not #803); label ""Rad-Dad"".
- Performer must be recognizably Michael: backwards black baseball cap, glasses, navy tank, chain, bare arms, NO tattoos on either arm.
- Green Mountain State video: v2 IS the video — nothing swaps into it without his explicit scene-by-scene approval. Lip-sync CANCELLED. Action clips for the 2 slots (overlook: looks out over valley, nods along; barn: leans on barn door with cows nearby, nods along), NO microphone. Finale: cliff/valley background stays exactly the same, figure becomes him in his singing outfit with THE CROWN on his head, no Splash jacket.

## Pool machines (roles, not addresses)
- Dayz: main media box, GTX 1660 Ti 6GB — video + image engine host.
- Dayz2: GTX 980 Ti 6GB — Radarr, backup engine host.
- Dayz3: GTX 970 4GB — Lidarr/MVidarr, engine host.
- G15 laptop: RTX 3050 4GB — fastest pool node.
- Dayz4: CPU-only, lightest duty — beachhead/control, NOT a video box.
- Dell: Michael's own PC — usable when it helps, but only after asking him first, per job.
- Splitting rule: spread work across ALL machines is the normal way, not a fallback — but ask Michael every time: all on one, or spread across all?
- Never force a model onto a card it doesn't fit; verify every engine with a real generation.

## Dashboard rules
- Mike's Flix dashboard: leave the Prowlarr yellow indexer light alone. No Jellyfin stop/message controls (removed at his direction — do not re-pitch).

## Standing prohibitions
- No 4K/2160p downloads, no 10-bit HEVC, no BR-DISK; upgrades manual.
- Instagram uploads of the 2 remaining reels: STOPPED at his order — do not retry until he says so.
- No Google sign-in from background jobs — live popup sign-ins with him present only.";

        /// <summary>System message for chat endpoints (Ollama /api/chat, OpenAI-compatible /chat/completions).</summary>
        public static string ForChatSystem() => Header + "\n\n" + Pack;

        /// <summary>Full prompt for single-prompt CLIs (OpenCode run). The tab prompt follows unchanged after the separator.</summary>
        public static string ForCli(string prompt) => Header + "\n\n" + Pack + "\n\n---\n\n" + prompt;
    }
}
