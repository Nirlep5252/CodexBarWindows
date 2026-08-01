using System;
using System.Collections.Generic;
using CodexBarWindows;
using SkiaSharp;
#if !CHARTPALETTE_TEST_HOST
using Microsoft.UI.Xaml;
using Windows.UI;
using Windows.UI.ViewManagement;
#endif

namespace CodexBar.WinUI;

/// <summary>
/// The colours the LiveCharts surfaces need, expressed as SkiaSharp colours.
/// </summary>
/// <remarks>
/// <para>
/// Category colours are PROVIDER-BRANDED, which the ported WinForms ramp was not: that one walked a
/// generic accent/success/warning list, so a Claude model and an OpenAI model could land on the same
/// hue and nothing on screen tied a bar back to the tool that produced it. A label is mapped to a
/// provider FAMILY first and then to a SLOT inside that family's ramp, so two Claude models read as
/// related and neither can be mistaken for a Codex one.
/// </para>
/// <para>
/// The ramps are <b>hand-picked constants</b>, one curated set PER THEME (<see cref="Families"/>).
/// An earlier revision generated them from a brand anchor by rotating hue and stepping lightness,
/// then ran the result through a contrast fixer. That is elegant and it does not work: every knob
/// interacted with every other one and with the fixer, so a "+6 lightness" variant was dragged
/// straight back onto its base in light mode, a "+7° hue" codex build was indistinguishable from
/// the model it forked, and two Codex models landed on the same hex in dark mode. None of it was
/// checkable by reading the code. Explicit tables are: the emitted set is finite, enumerable
/// (<see cref="CuratedColors"/>) and MECHANICALLY VERIFIED by the palette tests -
/// perceptual distance in CIE Lab, the same distance under simulated deuteranopia and protanopia,
/// and contrast against both card surfaces. Edit a hex below and those tests are what tell you
/// whether the edit was safe.
/// </para>
/// <para>
/// Nothing here is re-derived at render time, which is the other half of the point: a curated
/// colour is drawn exactly as written, so the hex in this file is the hex on screen and the hex in
/// the settings swatch.
/// </para>
/// <para>
/// Built from a specific element's <c>ActualTheme</c>, exactly like <see cref="FlyoutPalette"/> -
/// reading brushes out of <c>Application.Current.Resources</c> would freeze them to the app theme.
/// Consumers rebuild on <c>ActualThemeChanged</c>.
/// </para>
/// </remarks>
internal sealed class ChartPalette
{
    private readonly IReadOnlyDictionary<string, string> overrides;

    /// <summary>
    /// The accent is PASSED IN rather than read here so the whole colour half of this file stays
    /// free of WinUI types - that is what lets the palette tests compile this exact source into the
    /// test exe instead of testing a copy of it.
    /// </summary>
    internal ChartPalette(bool isDark, SKColor accent, IReadOnlyDictionary<string, string> overrides)
    {
        IsDark = isDark;
        this.overrides = overrides;

        Accent = accent;
        Success = isDark ? new SKColor(0x6C, 0xCB, 0x5F) : new SKColor(0x0F, 0x7B, 0x0F);
        Warning = isDark ? new SKColor(0xFF, 0xC8, 0x3D) : new SKColor(0x9D, 0x5D, 0x00);
        Danger = isDark ? new SKColor(0xFF, 0x7A, 0x7A) : new SKColor(0xC4, 0x2B, 0x1C);

        var shifted = ShiftHue(Accent, 60f);
        SeriesAlt = isDark ? Lighten(shifted, 0.20f) : Darken(shifted, 0.10f);

        Text = isDark ? new SKColor(0xFF, 0xFF, 0xFF) : new SKColor(0x1A, 0x1A, 0x1A);
        SecondaryText = isDark ? new SKColor(0xC5, 0xC5, 0xC5) : new SKColor(0x5D, 0x5D, 0x5D);
        Separator = isDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x18) : new SKColor(0x00, 0x00, 0x00, 0x16);
        // Deliberately NOT the card colour: the tooltip is drawn over the card, so a matching
        // tone made it invisible (verified on screen - the text floated with no surface).
        TooltipBackground = isDark ? new SKColor(0x3D, 0x3D, 0x3D) : new SKColor(0xFF, 0xFF, 0xFF);
        Track = isDark ? new SKColor(0xFF, 0xFF, 0xFF, 0x14) : new SKColor(0x00, 0x00, 0x00, 0x10);
    }

    public bool IsDark { get; }

    public SKColor Accent { get; }

    public SKColor Success { get; }

    public SKColor Warning { get; }

    public SKColor Danger { get; }

    /// <summary>
    /// Hue-rotated accent. Not used for model categories - those are branded by provider - but kept
    /// as the generic "second accent-derived series" any non-model chart can reach for.
    /// </summary>
    public SKColor SeriesAlt { get; }

    public SKColor Text { get; }

    public SKColor SecondaryText { get; }

    public SKColor Separator { get; }

    public SKColor TooltipBackground { get; }

    /// <summary>The unfilled part of a model row's bar.</summary>
    public SKColor Track { get; }

    /// <summary>
    /// Whether this label's colour was chosen by the user. A picked colour is used EXACTLY as
    /// picked, so callers skip <see cref="Nudge"/> for it - a silently lightened swatch would not
    /// match the hex the settings page shows.
    /// </summary>
    public bool IsOverridden(string label) => TryGetOverride(label, out _);

    /// <summary>
    /// The colour for one spend category ("gpt-5.5", "claude-opus-5", "gpt-5.4 fast", "regular", …).
    /// </summary>
    /// <remarks>
    /// The label is exactly what the ledger/scan grouped on (<c>ProviderSpendCategory.Label</c>), so
    /// it is a NORMALIZED model id, optionally with the " fast" suffix - plus the two bucket-level
    /// pseudo-categories ("regular"/"fast", used by providers that report no model split) and the
    /// pooled "other".
    /// </remarks>
    public SKColor ForCategory(string label)
    {
        // FIRST, before anything else: the fast handling below strips a suffix and the family
        // matcher rewrites the id, so an override on the RAW label "gpt-5.3-codex fast" has to be
        // consulted against the raw label or it would be unreachable. This is also the contract the
        // settings page shows - a picked colour is returned untouched, un-shaded and un-nudged.
        if (TryGetOverride(label, out var chosen))
        {
            return chosen;
        }

        var normalized = (label ?? string.Empty).Trim().ToLowerInvariant();

        // " fast" is a TIER of a model, not a model: it takes the fast half of the same slot, so
        // "gpt-5.4 fast" is visibly the same model as "gpt-5.4". Matched as a suffix (the label
        // builders only ever append it) rather than as "contains fast", which would also catch a
        // future model with "fast" in its name.
        var isFast = normalized.EndsWith(" fast", StringComparison.Ordinal);
        if (isFast)
        {
            normalized = normalized[..^" fast".Length].TrimEnd();
        }

        // The pooled tail is not a model and must never look like one: a desaturated slate that
        // competes with nothing. It is one flat colour, with no fast half - the pooled series is
        // built by the graphs window and is never suffixed.
        if (normalized is "other" or "")
        {
            return Neutral(IsDark);
        }

        // "regular" (and the bare "fast" it pairs with) is a whole-provider bucket emitted when a
        // provider reports no model breakdown at all. It stays on the system accent, exactly as it
        // always has and as the null-colour-key series does - there is no model to brand it by.
        // This is the ONE pair of emitted colours the palette tests cannot check, because the
        // Windows accent is the user's and can be any hue; see the test's own remark.
        if (normalized is "regular" or "fast")
        {
            return normalized == "fast" || isFast ? AccentFast() : Accent;
        }

        var entry = Resolve(normalized, IsDark);
        return isFast ? entry.Fast : entry.Base;
    }

    // ---------------------------------------------------------------- the curated tables

    /// <summary>Writes a ramp entry the way a designer reads it: <c>Rgb(0xD97757)</c>.</summary>
    private static SKColor Rgb(uint value) =>
        new((byte)(value >> 16), (byte)(value >> 8), (byte)value);

    /// <summary>One model slot: the model's colour and the colour its " fast" tier is drawn in.</summary>
    /// <remarks>
    /// The fast half is CURATED, not derived. Deriving it (a lightness step plus a contrast fixer)
    /// is what let the previous revision emit a fast tier that had been dragged back onto its own
    /// base in light mode. Here the pair is written down and the tests assert both halves of what
    /// "fast" has to mean: same hue family (related) and a real perceptual gap (distinguishable).
    /// </remarks>
    internal readonly record struct RampEntry(SKColor Base, SKColor Fast);

    /// <summary>
    /// One provider's chart identity: the ordered model tiers that map onto its ramp, plus the
    /// ramp itself in each theme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>Tiers</c> is MOST SPECIFIC FIRST and matched by substring against the normalized model
    /// id; the index of the first hit is the slot. THIS LIST IS APPEND-ONLY: inserting or
    /// reordering a token silently repaints every model at or after it, and a user who has learned
    /// "the orange one is Opus" has no way to know why it moved. Add new models at the END.
    /// </para>
    /// <para>
    /// A model the table has never heard of still lands in the right family and takes a HASHED
    /// slot, so adding a model is optional; adding a whole provider is one entry in
    /// <see cref="Families"/> plus its two ramps.
    /// </para>
    /// </remarks>
    /// <param name="Pinned">
    /// Ids whose slot is stated OUTRIGHT, consulted before the tier scan and append-only like it.
    /// </param>
    /// <remarks>
    /// <para>
    /// <c>Pinned</c> exists because a tier token names a model LINE, not a GENERATION, and it is
    /// matched by substring: "claude-opus-5" and "claude-opus-4-7" both hit the "opus" token and were
    /// therefore drawn in one colour - the reported "four Claude models, one salmon". Appending
    /// "opus-4-7" to <c>Tiers</c> cannot fix that (the scan stops at the first hit, which is still
    /// "opus" at index 0), and reordering <c>Tiers</c> would repaint every model after the insert.
    /// A pin says the answer instead of arranging for it.
    /// </para>
    /// <para>
    /// A pin cannot invent capacity. Claude's ramp has FOUR slots and four live lines
    /// (opus/fable/sonnet/haiku), so a fifth distinct Claude model necessarily shares a slot with one
    /// of them; the pin's job is only to pick WHICH, and it picks the line least likely to be
    /// on screen beside it. Wanting five simultaneously distinct Claude colours is a palette-CAPACITY
    /// question - a fifth ramp entry, re-run against the Lab-distance and contrast tests - not
    /// something to smuggle in here.
    /// </para>
    /// </remarks>
    private sealed record Family(
        string Key,
        string[] Tiers,
        RampEntry[] Dark,
        RampEntry[] Light,
        (string Id, int Slot)[]? Pinned = null)
    {
        public RampEntry[] Ramp(bool isDark) => isDark ? Dark : Light;

        public (string Id, int Slot)[] Pins => Pinned ?? [];
    }

    // ---- Anthropic ------------------------------------------------------------------------
    // Slot 0 is Anthropic's own mark colour #D97757 in dark mode - the same terracotta the flyout
    // draws the Claude glyph in (FlyoutPalette.ClaudeGlyph), so a Claude bar and the Claude icon
    // are the same colour - and a hue-preserving darkening of it in light mode, where the brand
    // value only clears 2.97:1 against a near-white card. Slot 0 is Opus deliberately: the model a
    // user sees most is the one that looks most like the provider. The remaining slots stay inside
    // a ~30° terracotta/warm-red band, so the family reads as one identity at a glance.
    private static readonly Family ClaudeFamily = new(
        "claude",
        ["opus", "fable", "sonnet", "haiku"],
        Dark:
        [
            new(Rgb(0xD97757), Rgb(0xAC5133)),
            new(Rgb(0xAC5D5D), Rgb(0xD58181)),
            new(Rgb(0xBC4548), Rgb(0xE76B6A)),
            new(Rgb(0xB75919), Rgb(0xE37D3D)),
        ],
        Light:
        [
            new(Rgb(0xBF6143), Rgb(0x721F07)),
            new(Rgb(0xD16831), Rgb(0xA4430C)),
            new(Rgb(0x8A0320), Rgb(0xE76465)),
            new(Rgb(0x854012), Rgb(0xCC7D4C)),
        ],
        // Every superseded Opus generation ("claude-opus-4-5/-4-6/-4-7/-4-8", and the dated
        // "claude-opus-4-20250514") contains the "opus" token and would otherwise be drawn in
        // slot 0 - Opus's own brand terracotta - beside the current Opus. They take the HAIKU slot
        // instead: of the four lines it is the one least likely to be plotted next to a previous
        // Opus generation, and slot 3 is a full ramp step away from slot 0 rather than a shade of it.
        Pinned: [("opus-4-", 3)]);

    // ---- OpenAI / Codex -------------------------------------------------------------------
    // The current OpenAI mark is monochrome, which is right for a tray glyph and useless for a
    // chart series (black/white IS the chart's text and background), so the family takes OpenAI's
    // long-standing product teal-green #10A37F - still a colour people recognise, and ~125° of Lab
    // hue away from Anthropic's terracotta. Longest ids first, so "gpt-5.6-sol" is not swallowed by
    // the "gpt-5.6" or "gpt-5" token.
    private static readonly Family CodexFamily = new(
        "codex",
        [
            "gpt-5.6-sol", "gpt-5.6-terra", "gpt-5.6-luna", "gpt-5.6",
            "gpt-5.5", "gpt-5.4", "gpt-5.3", "gpt-5.2", "gpt-5.1", "gpt-5",
            "o4-mini", "o3", "gpt-4.1",
        ],
        Dark:
        [
            new(Rgb(0x10A37F), Rgb(0x52D0A9)),
            new(Rgb(0x80C795), Rgb(0x31774B)),
            new(Rgb(0x25A05A), Rgb(0x6EDF94)),
            new(Rgb(0x94E0C4), Rgb(0x68B399)),
            new(Rgb(0x0D928E), Rgb(0x6DD9D4)),
        ],
        Light:
        [
            new(Rgb(0x009E7B), Rgb(0x006049)),
            new(Rgb(0x006333), Rgb(0x439C64)),
            new(Rgb(0x007371), Rgb(0x009C98)),
            new(Rgb(0x3F754F), Rgb(0x0D4825)),
            new(Rgb(0x004438), Rgb(0x00836F)),
        ]);

    // ---- Cursor ---------------------------------------------------------------------------
    // Cursor's mark is monochrome too and it has no product colour to borrow, so the hue is CHOSEN:
    // a blue-violet band, far from both other brands and from the warm end the fallback family
    // occupies. Three slots rather than five - Cursor exposes one composer line plus its own
    // models, and every extra slot is separation stolen from the ramps that carry more models.
    private static readonly Family CursorFamily = new(
        "cursor",
        ["composer", "cursor"],
        Dark:
        [
            new(Rgb(0x64679D), Rgb(0x9696D0)),
            new(Rgb(0x976DBF), Rgb(0xC396EC)),
            new(Rgb(0x9584E7), Rgb(0x6D5FBE)),
        ],
        Light:
        [
            new(Rgb(0x005BBA), Rgb(0x4A8CFF)),
            new(Rgb(0x9F71D2), Rgb(0x623A94)),
            new(Rgb(0x6A69A7), Rgb(0x454781)),
        ]);

    // ---- Fallback -------------------------------------------------------------------------
    // Everything the table has never heard of. A muted mauve band: low chroma says "this is not a
    // brand", and the hue is one nothing above is branded with, so an unrecognised model can never
    // be read as a Claude or a Codex one. It still has three slots so two unknown models do not
    // merge into one block.
    private static readonly Family UnknownFamily = new(
        "unknown",
        [],
        Dark:
        [
            new(Rgb(0xAA7DAD), Rgb(0xF0BFF2)),
            new(Rgb(0x7F6471), Rgb(0xD1B2C1)),
            new(Rgb(0xC5A1CA), Rgb(0x816086)),
        ],
        Light:
        [
            new(Rgb(0x4E3253), Rgb(0x87678B)),
            new(Rgb(0xAC81B2), Rgb(0x6F4775)),
            new(Rgb(0x9C7C8D), Rgb(0x6F5261)),
        ]);

    /// <summary>
    /// The families a NON-Anthropic id is matched against, in order. Claude is not in this list: it
    /// is decided by its own normaliser in <see cref="Resolve"/>, which is authoritative.
    /// </summary>
    private static readonly Family[] Families = [CodexFamily, CursorFamily];

    /// <summary>
    /// The pooled "other" tail, per theme. Near-greyscale on purpose (Lab chroma ~6) - it must never
    /// compete with a real model - and cool, so it reads as the opposite of the warm fallback ramp.
    /// </summary>
    /// <remarks>
    /// These two values are LOAD-BEARING and were not chosen by eye. A neutral is the hardest colour
    /// in the set to place for a dichromat, because simulation drags every low-chroma colour toward
    /// the same grey axis: nudging the light one up to a friendlier mid-grey (#5C6470, #767E8A, …)
    /// drops the palette's worst dichromat separation from 7.8 to under 1.5 against the fallback
    /// ramp. If you want a lighter pooled tail, move the fallback ramp first and re-run the tests.
    /// </remarks>
    private static SKColor Neutral(bool isDark) => isDark ? Rgb(0x9A9EAB) : Rgb(0x3D444D);

    // ---------------------------------------------------------------- label → family → slot

    /// <summary>
    /// Size/build suffixes that hang off a base model id.
    /// </summary>
    /// <remarks>
    /// <para>
    /// These are DIFFERENT MODELS that share a tier token: "gpt-5.1-codex" contains "gpt-5.1", and
    /// "claude-haiku-4.5-mini" would contain "haiku". Left alone they would land on their base
    /// model's slot and be drawn in exactly its colour. The previous revision tried to keep them
    /// "almost" their base - a +7° hue turn, a +6 lightness step - which is the worst of both:
    /// too small to separate, and small enough that the contrast pass could erase it entirely.
    /// </para>
    /// <para>
    /// Here a suffix moves the model to a DIFFERENT SLOT in the SAME family. Relatedness is carried
    /// by the family band (every Codex model is teal-green, every Claude model terracotta), which
    /// is a property no lightness clamp can undo; distinguishability is carried by the slot, which
    /// the ramp tests already guarantee. The offset is <c>1 + (index % (slots - 1))</c>, so it is
    /// never 0 modulo the ramp length whatever length a family has - a base and its codex build
    /// can therefore never collide, which is the one job this entry exists to do.
    /// </para>
    /// </remarks>
    private static readonly string[] VariantSuffixes = ["-codex", "-mini", "-nano", "-spark", "-pro", "-max"];

    /// <summary>
    /// Maps a normalized category label onto the ramp entry it owns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Uses the SAME normalisers the pricing tables and the ledger label with
    /// (<c>ClaudeModelPricing.NormalizeModelName</c> / <c>CodexModelPricing.NormalizeModelName</c>)
    /// rather than a third hand-rolled matcher, so an alias the pricing side already resolves
    /// ("opus" → "claude-opus-5", "openai/gpt-5.6" → "gpt-5.6-sol") colours as the model it IS.
    /// </para>
    /// <para>
    /// STABILITY, which is the whole contract: the result depends on the label and on the constant
    /// tables in this file, and on NOTHING ELSE - not on which other models are being plotted, not
    /// on the order the chart stacked them, not on the process. So a model keeps its colour between
    /// sessions and between two charts that happen to show different neighbours.
    /// </para>
    /// </remarks>
    private static RampEntry Resolve(string label, bool isDark)
    {
        // The build/size suffix comes off FIRST, and the model is then coloured from its BASE
        // model's slot plus a fixed offset. Peeling first is what makes the offset mean anything:
        // "gpt-5.6" is an alias for "gpt-5.6-sol" and resolves to slot 0, while the raw
        // "gpt-5.6-mini" matches the "gpt-5.6" tier token at slot 3 - offsetting from 3 walked
        // straight back onto slot 0, which is its own base model. Measured, not imagined: that is
        // the collision this arrangement was caught making.
        var variantIndex = -1;
        var normalized = label;

        // Matched at the END, because that is where a build/size suffix goes, and skipped entirely
        // for an id the tables already name as a model of its own: "o4-mini" ends in "-mini" and is
        // a model, not a variant of a thing called "o4". Without that guard it and "o4-mini-mini"
        // both peel down to the same base and are drawn identically.
        if (!IsListedTier(label))
        {
            for (var index = 0; index < VariantSuffixes.Length; index++)
            {
                if (label.EndsWith(VariantSuffixes[index], StringComparison.Ordinal))
                {
                    variantIndex = index;
                    normalized = label[..^VariantSuffixes[index].Length];
                    break;
                }
            }
        }

        // Anthropic first: its normaliser answers definitively (every Anthropic id - first-party,
        // Bedrock or Vertex - starts with "claude" once normalized), so it cannot claim a Codex id.
        var claudeId = ClaudeModelPricing.NormalizeModelName(normalized).ToLowerInvariant();
        var isClaude = ClaudeModelPricing.IsAnthropicModel(claudeId);
        var id = isClaude ? claudeId : CodexModelPricing.NormalizeModelName(normalized);

        var family = UnknownFamily;
        if (isClaude)
        {
            family = ClaudeFamily;
        }
        else
        {
            foreach (var candidate in Families)
            {
                foreach (var tier in candidate.Tiers)
                {
                    if (id.Contains(tier, StringComparison.Ordinal))
                    {
                        family = candidate;
                        goto matched;
                    }
                }
            }
        }

    matched:
        var ramp = family.Ramp(isDark);
        var slots = ramp.Length;
        var tierIndex = -1;
        for (var index = 0; index < family.Tiers.Length; index++)
        {
            if (id.Contains(family.Tiers[index], StringComparison.Ordinal))
            {
                tierIndex = index;
                break;
            }
        }

        // An unlisted model still belongs to its family; only WHICH slot is hashed.
        var slot = tierIndex >= 0 ? tierIndex % slots : StableSlot(id, slots);

        // A PIN outranks the tier scan, which cannot separate two generations of one line - see
        // Family.Pinned. Checked after the scan rather than before it so the pin is expressed as
        // "this id belongs in slot N", independent of where its line happens to sit.
        foreach (var (pinnedId, pinnedSlot) in family.Pins)
        {
            if (id.Contains(pinnedId, StringComparison.Ordinal))
            {
                slot = pinnedSlot % slots;
                break;
            }
        }

        if (variantIndex >= 0)
        {
            // Never 0 modulo the ramp length, whatever length a family has, so a model and its
            // codex/mini build can never land on the same slot. Two DIFFERENT suffixes of one base
            // model can still meet when a family has fewer slots than this table has entries - a
            // finite curated ramp cannot separate more models than it has colours - and that is
            // what the draw-time Nudge is left in place for.
            slot = (slot + 1 + (variantIndex % Math.Max(1, slots - 1))) % slots;
        }

        return ramp[slot];
    }

    /// <summary>
    /// Whether the tables already name this exact id as a model in its own right.
    /// </summary>
    /// <remarks>
    /// EXACT equality, unlike the tier matching itself, which is by substring: the question here is
    /// "is this id a model" and "o4-mini-mini" contains a listed model without being one.
    /// </remarks>
    private static bool IsListedTier(string id)
    {
        foreach (var family in new[] { ClaudeFamily, CodexFamily, CursorFamily })
        {
            foreach (var tier in family.Tiers)
            {
                if (string.Equals(id, tier, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// FNV-1a over the model id.
    /// </summary>
    /// <remarks>
    /// Written out rather than calling <c>string.GetHashCode</c>, which .NET RANDOMISES per process:
    /// an unlisted model would then be a different colour on every launch, which is precisely the
    /// stability the curated tables exist to provide.
    /// </remarks>
    private static int StableSlot(string value, int slots)
    {
        var hash = 2166136261u;
        foreach (var character in value)
        {
            hash ^= character;
            hash *= 16777619u;
        }

        return (int)(hash % (uint)Math.Max(1, slots));
    }

    // ---------------------------------------------------------------- accent-derived tiers

    /// <summary>
    /// The "fast" half of the accent-coloured bucket series.
    /// </summary>
    /// <remarks>
    /// The only DERIVED colour left in the file, because its base is the user's Windows accent and
    /// so cannot be written down. The move is away from the card surface (lighter in dark mode,
    /// darker in light mode) since a fast segment stacks directly on top of its regular one, and it
    /// reverses for an accent already close to the far end, where pushing further would make it
    /// near-white or near-black.
    /// </remarks>
    private SKColor AccentFast()
    {
        Accent.ToHsl(out var hue, out var saturation, out var lightness);
        var away = IsDark ? +18f : -18f;
        var delta = Contrast(Accent, IsDark ? DarkSurface : LightSurface) < 6.0f ? away : -away;

        return EnsureContrast(
            SKColor.FromHsl(hue, Math.Max(0f, saturation - 8f), Math.Clamp(lightness + delta, 6f, 94f), Accent.Alpha),
            IsDark,
            FillContrast);
    }

    // ---------------------------------------------------------------- surfaces and contrast

    /// <summary>
    /// The surface a chart fill actually sits on - the graphs window card, in each theme. Used only
    /// to measure contrast; nothing is drawn in it.
    /// </summary>
    internal static readonly SKColor DarkSurface = new(0x20, 0x20, 0x20);

    internal static readonly SKColor LightSurface = new(0xF9, 0xF9, 0xF9);

    /// <summary>
    /// The contrast floor a curated colour holds against the card of ITS OWN theme: 3:1, the WCAG
    /// 1.4.11 non-text floor, because a series fill is a graphical object the chart cannot be read
    /// without.
    /// </summary>
    internal const float OwnThemeContrast = 3.0f;

    /// <summary>
    /// The floor a curated colour holds against the card of the OTHER theme: 1.45:1.
    /// </summary>
    /// <remarks>
    /// Not decorative. The settings page previews a model's colour from the hex the graphs window
    /// RECORDED as drawn (<c>ChartCategoryCatalog</c>), and that hex may have been recorded while
    /// the graphs window was in the opposite theme - so a dark-theme fill really can be painted as
    /// a swatch on a light settings card. 1.45 is deliberately far below 3:1: a colour cannot clear
    /// 3:1 against BOTH #202020 and #F9F9F9 (the best any single colour manages against both at
    /// once is ~3.87, and only in one narrow luminance band, which would collapse lightness as a
    /// discriminator and take the ramps with it). 1.45 is the floor at which a filled swatch still
    /// has a visible edge against the card it is on, which is all the swatch has to do.
    /// </remarks>
    internal const float OtherThemeContrast = 1.45f;

    /// <summary>The floor <see cref="Nudge"/> and <see cref="AccentFast"/> keep while moving a colour.</summary>
    private const float FillContrast = 2.5f;

    /// <summary>WCAG relative-luminance contrast ratio between two opaque colours.</summary>
    internal static float Contrast(SKColor first, SKColor second)
    {
        var a = Luminance(first);
        var b = Luminance(second);
        return (Math.Max(a, b) + 0.05f) / (Math.Min(a, b) + 0.05f);
    }

    private static float Luminance(SKColor color) =>
        (0.2126f * Linear(color.Red)) + (0.7152f * Linear(color.Green)) + (0.0722f * Linear(color.Blue));

    private static float Linear(byte channel)
    {
        var value = channel / 255f;
        return value <= 0.03928f ? value / 12.92f : MathF.Pow((value + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>
    /// Walks a colour's lightness away from the chart surface until it clears <paramref name="minRatio"/>.
    /// </summary>
    /// <remarks>
    /// Only ever applied to a colour this file DERIVED at render time (the accent's fast tier, a
    /// collision nudge). It is deliberately NOT applied to the curated ramps: a fixer that can move
    /// a written-down colour is exactly how the previous revision ended up with two models on one
    /// hex, and the ramps clear their floors by construction, which the tests assert.
    /// </remarks>
    private static SKColor EnsureContrast(SKColor color, bool isDark, float minRatio)
    {
        for (var guard = 0; guard < 48 && Contrast(color, isDark ? DarkSurface : LightSurface) < minRatio; guard++)
        {
            color.ToHsl(out var hue, out var saturation, out var lightness);
            var moved = Math.Clamp(lightness + (isDark ? 2f : -2f), 0f, 100f);
            if (Math.Abs(moved - lightness) < 0.01f)
            {
                break;
            }

            color = SKColor.FromHsl(hue, saturation, moved, color.Alpha);
        }

        return color;
    }

    // ---------------------------------------------------------------- overrides and helpers

    /// <summary>
    /// Parses a stored <c>"#RRGGBB"</c> override. Anything else is treated as absent so a corrupt
    /// entry degrades to the automatic colour instead of throwing while a chart is being drawn.
    /// </summary>
    public static bool TryParseHex(string? hex, out SKColor color)
    {
        color = default;
        if (UiSettings.NormalizeHexColor(hex) is not { } normalized)
        {
            return false;
        }

        color = new SKColor(
            Convert.ToByte(normalized.Substring(1, 2), 16),
            Convert.ToByte(normalized.Substring(3, 2), 16),
            Convert.ToByte(normalized.Substring(5, 2), 16));
        return true;
    }

    public static string ToHex(SKColor color) => $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2}";

    private bool TryGetOverride(string label, out SKColor color)
    {
        color = default;
        return !string.IsNullOrWhiteSpace(label) &&
            overrides.TryGetValue(label.Trim(), out var hex) &&
            TryParseHex(hex, out color);
    }

    /// <summary>
    /// Separates two categories that landed on the same colour, so a stack never shows two
    /// touching segments the eye reads as one bar.
    /// </summary>
    /// <remarks>
    /// A last resort, not a mechanism: with curated ramps the only way two co-plotted labels share
    /// a colour is a family with more models on screen than it has slots, and the graphs window
    /// plots at most seven series in total. Only ever LIGHTENS/DARKENS, so the category's identity
    /// hue survives; the direction is checked against the fill floor and reversed rather than
    /// allowed to sink a segment into the background.
    /// </remarks>
    public static SKColor Nudge(SKColor color, int step, bool isDark)
    {
        if (step <= 0)
        {
            return color;
        }

        var amount = Math.Min(0.45f, 0.16f * step);
        var moved = isDark ? Darken(color, amount) : Lighten(color, amount);
        if (Contrast(moved, isDark ? DarkSurface : LightSurface) < FillContrast)
        {
            moved = isDark ? Lighten(color, amount) : Darken(color, amount);
        }

        return EnsureContrast(moved, isDark, FillContrast);
    }

    public static SKColor Lighten(SKColor color, float amount)
    {
        color.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, Math.Clamp(l + (amount * 100f), 0f, 100f), color.Alpha);
    }

    public static SKColor Darken(SKColor color, float amount)
    {
        color.ToHsl(out var h, out var s, out var l);
        return SKColor.FromHsl(h, s, Math.Clamp(l - (amount * 100f), 0f, 100f), color.Alpha);
    }

    public static SKColor ShiftHue(SKColor color, float degrees)
    {
        color.ToHsl(out var h, out var s, out var l);
        var hue = (h + degrees) % 360f;
        if (hue < 0)
        {
            hue += 360f;
        }

        return SKColor.FromHsl(hue, s, l, color.Alpha);
    }

    // ---------------------------------------------------------------- test surface

    /// <summary>
    /// Every colour <see cref="ForCategory"/> can emit in one theme that is neither a user override
    /// nor derived from the user's Windows accent, keyed by where it came from.
    /// </summary>
    /// <remarks>
    /// This exists so the palette tests can check the WHOLE emitted set rather than the handful of
    /// labels someone thought to write down. It is the enumeration the "no two colours are
    /// confusable" and "everything clears its contrast floor" tests iterate.
    /// </remarks>
    internal static IReadOnlyList<(string Key, SKColor Color)> CuratedColors(bool isDark)
    {
        var colors = new List<(string, SKColor)> { ("other", Neutral(isDark)) };

        foreach (var family in new[] { ClaudeFamily, CodexFamily, CursorFamily, UnknownFamily })
        {
            var ramp = family.Ramp(isDark);
            for (var slot = 0; slot < ramp.Length; slot++)
            {
                colors.Add(($"{family.Key}[{slot}]", ramp[slot].Base));
                colors.Add(($"{family.Key}[{slot}] fast", ramp[slot].Fast));
            }
        }

        return colors;
    }

    /// <summary>The tier tokens and ramp length of each family, so a test can walk every slot.</summary>
    internal static IReadOnlyList<(string Key, string[] Tiers, int Slots)> FamilyShapes(bool isDark) =>
        Array.ConvertAll(
            new[] { ClaudeFamily, CodexFamily, CursorFamily, UnknownFamily },
            family => (family.Key, family.Tiers, family.Ramp(isDark).Length));

    /// <summary>The size/build suffixes a test must prove never collide with their base model.</summary>
    internal static IReadOnlyList<string> VariantSuffixesForTests => VariantSuffixes;

#if !CHARTPALETTE_TEST_HOST

    /// <summary>
    /// Builds the palette for one element's resolved theme, with the user's per-model colour
    /// overrides folded in.
    /// </summary>
    /// <remarks>
    /// The overrides are PASSED IN rather than read from <c>AppTheme.Settings</c> here, keeping
    /// this type's only dependency the element it is themed from - the same UI → settings
    /// direction <see cref="UiSettings"/> documents.
    /// </remarks>
    public static ChartPalette For(FrameworkElement element, IReadOnlyDictionary<string, string>? overrides = null) =>
        new(
            element.ActualTheme == ElementTheme.Dark ||
                (element.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark),
            ToSkia(SystemAccent(
                element.ActualTheme == ElementTheme.Dark ||
                    (element.ActualTheme == ElementTheme.Default && Application.Current.RequestedTheme == ApplicationTheme.Dark))),
            overrides ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static SKColor ToSkia(Color color) => new(color.R, color.G, color.B, color.A);

    /// <summary>The user's Windows accent, nudged the same way the flyout meters nudge it.</summary>
    private static Color SystemAccent(bool isDark)
    {
        try
        {
            var settings = new UISettings();
            return settings.GetColorValue(isDark ? UIColorType.AccentLight2 : UIColorType.AccentDark1);
        }
        catch (Exception)
        {
            return isDark
                ? Color.FromArgb(0xFF, 0x60, 0xB0, 0xFF)
                : Color.FromArgb(0xFF, 0x0F, 0x6C, 0xBD);
        }
    }

#endif
}
