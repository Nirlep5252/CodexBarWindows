using CodexBar.WinUI;
using SkiaSharp;

/// <summary>
/// Mechanical checks on <see cref="ChartPalette"/>'s curated ramps.
/// </summary>
/// <remarks>
/// <para>
/// ChartPalette.cs is COMPILED INTO this exe (see the <c>Compile Include</c> in the csproj): its
/// colour half is deliberately free of every WinUI type so these checks run against the source the
/// shell ships rather than a copy that can drift. Everything the palette can emit is enumerable
/// (<see cref="ChartPalette.CuratedColors"/>), which is the whole reason the ramps are hand-written
/// constants: the generated ramp they replaced could not be checked at all without re-deriving it,
/// and it shipped with two models on one hex because nobody could.
/// </para>
/// <para>
/// WHY CIE Lab AND NOT RGB DISTANCE: sRGB is not perceptually uniform. Two blues 40 units apart in
/// RGB can be indistinguishable while two greens 20 apart are obvious, so an RGB metric would
/// happily pass a palette a person cannot read. Every distance below is CIE76 dE - plain Euclidean
/// distance in CIE Lab, D65 white, converted from sRGB through the standard linearisation. CIE76 is
/// coarser than CIEDE2000 and its known failure is OVERSTATING distance for very saturated blues,
/// so a floor set on it is optimistic exactly there; that is tolerable because the floors are set
/// from what these ramps actually measure with margin (see each constant), not from a textbook
/// just-noticeable figure, and only one family sits in the blue region.
/// </para>
/// </remarks>
internal static class ChartPaletteTests
{
    /// <summary>
    /// The floor every pair of emitted colours clears inside one theme, for normal colour vision.
    /// </summary>
    /// <remarks>
    /// CHOSEN, NOT INHERITED. A just-noticeable difference is dE≈2.3 and a categorical chart palette
    /// would ideally want 20+, but the palette emits 31 colours per theme (four provider ramps x
    /// their slots x a regular/fast pair each, plus the pooled tail) and every one of them must also
    /// clear 3:1 against its card, which pins the whole set inside a narrow slab of Lab. The curated
    /// ramps measure a worst pair of 13.2 (light) and 13.7 (dark); the floor is 12.0, so an edit has
    /// a little room before it fails and no more. Deliberately NOT lowered further - a smaller
    /// number here would be picking a threshold to pass rather than to mean something. Two colours
    /// 12 apart are plainly different as large solid fills, which is what a bar segment is.
    /// </remarks>
    private const double MinDeltaE = 12.0;

    /// <summary>
    /// The same floor after simulating dichromatic vision.
    /// </summary>
    /// <remarks>
    /// DELIBERATELY LOWER, and this is the honest part. A dichromat loses one cone class, which
    /// collapses a whole axis of Lab, so a set that is 13 apart in normal vision cannot also be 13
    /// apart under simulation unless it stops using hue - and hue is what tells a Claude bar from a
    /// Codex one. The ramps measure 7.8 (light) and 8.0 (dark) under the worse of
    /// deuteranopia/protanopia, so the floor is 7.0. That is far above the dE≈2.3 detection
    /// threshold and well short of comfortable: the residual separation is carried by LIGHTNESS,
    /// which no colour blindness removes, so a deuteranope reads the chart by lightness order plus
    /// the legend rather than by hue. Said out loud rather than papered over.
    /// </remarks>
    private const double MinDichromatDeltaE = 7.0;

    /// <summary>
    /// How far a " fast" tier may sit from the model it belongs to, and how far it must.
    /// </summary>
    /// <remarks>
    /// BOTH ENDS MATTER, and the revision this replaced got both wrong: derived by a lightness step
    /// and then run through a contrast fixer, a fast tier could be dragged back ONTO its base in
    /// light mode. The lower bound is the same 12 the rest of the palette holds, so a fast segment
    /// stacked directly on its regular one is visibly its own band; the upper bound stops it
    /// drifting so far that it stops reading as the same model. Measured range: 14.1 to 30.7.
    /// </remarks>
    private const double MinFastDeltaE = 12.0;

    private const double MaxFastDeltaE = 36.0;

    /// <summary>
    /// How far a fast tier's Lab hue may sit from its base model's.
    /// </summary>
    /// <remarks>
    /// Relatedness is a HUE property, not a distance one - the fast tier is the same colour, lighter
    /// or darker. 10° is well inside the band each provider family occupies, so a fast tier can
    /// never wander into a sibling slot's hue, let alone another provider's. Measured worst: 1.3°.
    /// </remarks>
    private const double MaxFastHueDegrees = 10.0;

    public static void ColorsStayApart()
    {
        foreach (var isDark in new[] { true, false })
        {
            var colors = ChartPalette.CuratedColors(isDark);
            for (var i = 0; i < colors.Count; i++)
            {
                for (var j = i + 1; j < colors.Count; j++)
                {
                    var distance = DeltaE(colors[i].Color, colors[j].Color);
                    if (distance < MinDeltaE)
                    {
                        throw new InvalidOperationException(
                            $"{Theme(isDark)}: {Describe(colors[i])} and {Describe(colors[j])} are only " +
                            $"dE {distance:F1} apart (floor {MinDeltaE})");
                    }
                }
            }
        }
    }

    public static void ColorsStayApartForDichromats()
    {
        foreach (var isDark in new[] { true, false })
        {
            var colors = ChartPalette.CuratedColors(isDark);
            for (var i = 0; i < colors.Count; i++)
            {
                for (var j = i + 1; j < colors.Count; j++)
                {
                    foreach (var (name, simulate) in Simulations)
                    {
                        var distance = DeltaE(simulate(colors[i].Color), simulate(colors[j].Color));
                        if (distance < MinDichromatDeltaE)
                        {
                            throw new InvalidOperationException(
                                $"{Theme(isDark)}/{name}: {Describe(colors[i])} and {Describe(colors[j])} are " +
                                $"only dE {distance:F1} apart (floor {MinDichromatDeltaE})");
                        }
                    }
                }
            }
        }
    }

    public static void ClearsContrastFloors()
    {
        foreach (var isDark in new[] { true, false })
        {
            var own = isDark ? ChartPalette.DarkSurface : ChartPalette.LightSurface;
            var other = isDark ? ChartPalette.LightSurface : ChartPalette.DarkSurface;

            foreach (var entry in ChartPalette.CuratedColors(isDark))
            {
                var ownRatio = ChartPalette.Contrast(entry.Color, own);
                if (ownRatio < ChartPalette.OwnThemeContrast)
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: {Describe(entry)} is only {ownRatio:F2}:1 against its own card " +
                        $"(floor {ChartPalette.OwnThemeContrast})");
                }

                // The settings swatch can paint a hex the graphs window recorded in the OTHER theme,
                // so nothing may vanish into the opposite card either. See ChartPalette's remark on
                // why this floor is far below 3:1 rather than equal to it.
                var otherRatio = ChartPalette.Contrast(entry.Color, other);
                if (otherRatio < ChartPalette.OtherThemeContrast)
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: {Describe(entry)} is only {otherRatio:F2}:1 against the opposite " +
                        $"card (floor {ChartPalette.OtherThemeContrast})");
                }
            }
        }
    }

    public static void FastTiersStayRelatedButSeparable()
    {
        foreach (var isDark in new[] { true, false })
        {
            var palette = Palette(isDark);
            foreach (var (key, tiers, slots) in ChartPalette.FamilyShapes(isDark))
            {
                // Driven through ForCategory rather than read off the ramp table, so this also
                // proves the " fast" suffix routes to the fast half of the SAME slot.
                for (var slot = 0; slot < slots; slot++)
                {
                    var label = ProbeLabel(key, tiers, slot);
                    var regular = palette.ForCategory(label);
                    var fast = palette.ForCategory(label + " fast");

                    var distance = DeltaE(regular, fast);
                    if (distance < MinFastDeltaE || distance > MaxFastDeltaE)
                    {
                        throw new InvalidOperationException(
                            $"{Theme(isDark)}: \"{label}\" and its fast tier are dE {distance:F1} apart " +
                            $"(want {MinFastDeltaE}..{MaxFastDeltaE})");
                    }

                    var drift = HueGap(regular, fast);
                    if (drift > MaxFastHueDegrees)
                    {
                        throw new InvalidOperationException(
                            $"{Theme(isDark)}: \"{label}\"'s fast tier drifts {drift:F1}° in hue " +
                            $"(max {MaxFastHueDegrees}) - it no longer reads as the same model");
                    }
                }
            }
        }
    }

    public static void BrandsAModelByItsProvider()
    {
        // A bar's colour has to say which tool produced it, so every id a provider can emit must
        // land in that provider's ramp and in nobody else's.
        var expectations = new (string Label, string Family)[]
        {
            ("claude-opus-5", "claude"),
            ("claude-sonnet-4.5", "claude"),
            ("opus", "claude"),
            ("claude-haiku-4.5 fast", "claude"),
            ("gpt-5.6-sol", "codex"),
            ("gpt-5.4", "codex"),
            ("gpt-5.1-codex", "codex"),
            ("o3", "codex"),
            ("composer-1", "cursor"),
            ("some-model-nobody-has-heard-of", "unknown"),
            ("llama-9", "unknown"),
        };

        foreach (var isDark in new[] { true, false })
        {
            var palette = Palette(isDark);
            var ramps = RampsByFamily(isDark);

            foreach (var (label, family) in expectations)
            {
                var color = palette.ForCategory(label);
                if (Array.IndexOf(ramps[family], color) < 0)
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: \"{label}\" was drawn {ChartPalette.ToHex(color)}, which is not in " +
                        $"the {family} ramp");
                }

                foreach (var (other, colors) in ramps)
                {
                    if (other != family && Array.IndexOf(colors, color) >= 0)
                    {
                        throw new InvalidOperationException(
                            $"{Theme(isDark)}: \"{label}\" was drawn {ChartPalette.ToHex(color)}, which the " +
                            $"{other} ramp also uses");
                    }
                }
            }

            // The pooled tail competes with nothing, so it must not be any model's colour either.
            var pooled = palette.ForCategory("other");
            foreach (var (family, colors) in ramps)
            {
                if (Array.IndexOf(colors, pooled) >= 0)
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: the pooled \"other\" colour is also a {family} model colour");
                }
            }
        }
    }

    public static void KeepsVariantsOffTheirBaseModel()
    {
        // The finding this test exists for: a "-codex" build used to be its base model plus a 7°
        // hue turn, which is not a difference anyone can see, and because the two ended up unequal
        // the collision nudge never fired either. Every known build/size suffix must move the model
        // to a different slot, in every family, for every named tier - and its fast tier with it.
        foreach (var isDark in new[] { true, false })
        {
            var palette = Palette(isDark);
            foreach (var (key, tiers, slots) in ChartPalette.FamilyShapes(isDark))
            {
                foreach (var tier in tiers)
                {
                    foreach (var suffix in ChartPalette.VariantSuffixesForTests)
                    {
                        if (palette.ForCategory(tier) == palette.ForCategory(tier + suffix))
                        {
                            throw new InvalidOperationException(
                                $"{Theme(isDark)}: \"{tier}{suffix}\" is drawn in exactly its base model's " +
                                $"colour {ChartPalette.ToHex(palette.ForCategory(tier))}");
                        }

                        if (palette.ForCategory(tier + " fast") == palette.ForCategory(tier + suffix + " fast"))
                        {
                            throw new InvalidOperationException(
                                $"{Theme(isDark)}: \"{tier}{suffix} fast\" collides with \"{tier} fast\"");
                        }
                    }
                }
            }
        }
    }

    public static void AssignsColorsIndependently()
    {
        // Stability is what makes a curated ramp worth having: a model's colour depends on the
        // model's own name and on nothing else. Not on which other models the chart happens to show,
        // not on the order they were stacked, not on the process. Checked in different company, in
        // reverse order, and against a palette carrying overrides for UNRELATED labels.
        var labels = new[]
        {
            "claude-opus-5", "claude-sonnet-4.5", "gpt-5.6-sol", "gpt-5.4", "gpt-5.4 fast",
            "gpt-5.1-codex", "composer-1", "mystery-model-7", "other",
        };

        foreach (var isDark in new[] { true, false })
        {
            var reference = new Dictionary<string, SKColor>(StringComparer.Ordinal);
            foreach (var label in labels)
            {
                reference[label] = Palette(isDark).ForCategory(label);
            }

            var crowded = new ChartPalette(
                isDark,
                Accent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["gpt-5.5"] = "#123456",
                    ["claude-haiku-4.5"] = "#654321",
                    ["totally-different-model"] = "#ABCDEF",
                });

            for (var index = labels.Length - 1; index >= 0; index--)
            {
                var label = labels[index];
                var again = crowded.ForCategory(label);
                if (again != reference[label])
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: \"{label}\" moved from {ChartPalette.ToHex(reference[label])} to " +
                        $"{ChartPalette.ToHex(again)} when other models were present");
                }
            }

            // A freshly built palette agrees, which is what "survives a restart" means here: nothing
            // in the assignment path is process-dependent, in particular no randomised string hash.
            foreach (var label in labels)
            {
                if (Palette(isDark).ForCategory(label) != reference[label])
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: \"{label}\" is not stable across two palettes");
                }
            }
        }
    }

    public static void ReturnsOverridesExactly()
    {
        // MUST NOT REGRESS: the override is consulted FIRST and returned untouched - no shading, no
        // contrast clamp, no nudge - because the settings page promises that exact hex. And it is
        // keyed on the RAW label, " fast" suffix and all: a changed key would silently orphan every
        // colour the user has already picked.
        foreach (var isDark in new[] { true, false })
        {
            var palette = new ChartPalette(
                isDark,
                Accent,
                new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    // #010203 clears no contrast floor in either theme, which is the point: an
                    // override is the user's decision and the palette does not second-guess it.
                    ["gpt-5.4"] = "#010203",
                    ["gpt-5.3-codex fast"] = "#FEFEFE",
                    ["other"] = "#00FF00",
                });

            foreach (var (label, hex) in new[]
            {
                ("gpt-5.4", "#010203"),
                ("gpt-5.3-codex fast", "#FEFEFE"),
                ("other", "#00FF00"),
            })
            {
                if (!palette.IsOverridden(label))
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: \"{label}\" was not reported as overridden");
                }

                var drawn = ChartPalette.ToHex(palette.ForCategory(label));
                if (!string.Equals(drawn, hex, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"{Theme(isDark)}: \"{label}\" was overridden to {hex} but drawn {drawn}");
                }
            }

            if (palette.IsOverridden("gpt-5.5"))
            {
                throw new InvalidOperationException("an unset label reported itself as overridden");
            }
        }
    }

    // ---------------------------------------------------------------- fixtures

    /// <summary>
    /// A fixed stand-in for the Windows accent.
    /// </summary>
    /// <remarks>
    /// The accent-derived series - "regular", the bare "fast", and the null-colour-key series the
    /// graphs window falls back to - are the ONE thing here that cannot be verified: the accent is
    /// whatever the user picked in Windows, so it can be any hue at any lightness and could in
    /// principle land near a curated colour. They are excluded from the distance and contrast sweeps
    /// for that reason, deliberately and out loud rather than quietly skipped. They are also the
    /// coarsest thing on the chart - a whole provider bucket, plotted only when no model breakdown
    /// exists at all - so a near miss costs a legend lookup, not a misread bar.
    /// </remarks>
    private static readonly SKColor Accent = new(0x60, 0xB0, 0xFF);

    /// <summary>
    /// The models a real account actually plots must be pairwise distinct - which the ramp tests
    /// alone never checked.
    /// </summary>
    /// <remarks>
    /// This is the test whose absence let "four Claude models in one salmon" ship. Everything else
    /// here enumerates <c>CuratedColors</c>, i.e. the SLOTS, and a slot set can be perfectly
    /// separated while two live model ids both resolve into the same slot - which is exactly what
    /// happened: the tier tokens name a model LINE ("opus") and are matched by substring, so
    /// claude-opus-5 and claude-opus-4-7 were the same hex before the draw-time nudge.
    /// <para>
    /// Distinctness is asserted on the RESOLVED colour, not on the slot index, because that is what
    /// the user sees. Any future generation that cannot be separated inside its family's ramp is a
    /// palette-CAPACITY problem and will fail here, loudly, instead of merging on screen.
    /// </para>
    /// </remarks>
    public static void GivesEveryLiveModelItsOwnColor()
    {
        // The four Claude models a current account actually plots. Anthropic's ramp has four slots
        // and four model LINES, so this cohort is exactly at capacity and must resolve cleanly.
        var claude = new[] { "claude-opus-5", "claude-fable-5", "claude-opus-4-7", "claude-sonnet-5" };

        foreach (var isDark in new[] { true, false })
        {
            var palette = Palette(isDark);
            for (var first = 0; first < claude.Length; first++)
            {
                for (var second = first + 1; second < claude.Length; second++)
                {
                    var a = palette.ForCategory(claude[first]);
                    var b = palette.ForCategory(claude[second]);
                    if (a == b)
                    {
                        throw new InvalidOperationException(
                            $"{Theme(isDark)}: \"{claude[first]}\" and \"{claude[second]}\" both resolve to " +
                            $"{ChartPalette.ToHex(a)} - a ramp slot is carrying two live models");
                    }
                }
            }

            // CAPACITY, stated rather than wished away. The Codex family lists thirteen tiers over a
            // five-slot ramp, so two co-plotted Codex models genuinely CAN share a slot
            // (gpt-5.6-sol and gpt-5.4 are one such pair) and the draw-time Nudge is the only thing
            // that separates them. That recovery is asserted here, and it is why the graphs window
            // computes its nudge steps once per period from a stable ordering rather than per pass:
            // an ordering-dependent nudge drew the same model in two colours.
            var collided = palette.ForCategory("gpt-5.6-sol");
            if (collided == palette.ForCategory("gpt-5.4") &&
                ChartPalette.Nudge(collided, 1, isDark) == collided)
            {
                throw new InvalidOperationException(
                    $"{Theme(isDark)}: two Codex models share slot {ChartPalette.ToHex(collided)} and the nudge " +
                    "does not move it, so they would be drawn as one bar");
            }
        }
    }

    private static ChartPalette Palette(bool isDark) =>
        new(isDark, Accent, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase));

    private static string Theme(bool isDark) => isDark ? "dark" : "light";

    private static string Describe((string Key, SKColor Color) entry) =>
        $"{entry.Key} {ChartPalette.ToHex(entry.Color)}";

    /// <summary>A label guaranteed to land on <paramref name="slot"/> of one family's ramp.</summary>
    private static string ProbeLabel(string family, string[] tiers, int slot)
    {
        // Every slot must be REACHED, not assumed reachable. A tier token usually maps to its own
        // index, but not always - an alias resolves to another model first ("gpt-5.6" IS
        // "gpt-5.6-sol") - so each candidate is checked against the slot's actual colour. Taking the
        // index on trust would let this test probe slot 0 five times and never look at slot 3.
        // The assignment is theme-independent, so a probe found in dark mode addresses the same slot
        // in light mode.
        var probe = Palette(true);
        var wanted = BaseColors(family, isDark: true)[slot];

        foreach (var tier in tiers)
        {
            if (probe.ForCategory(tier) == wanted)
            {
                return tier;
            }

            // Slots past the end of a short tier list are only reachable through the variant offset,
            // which is exactly how a real "-mini"/"-codex" build gets there.
            foreach (var suffix in ChartPalette.VariantSuffixesForTests)
            {
                if (probe.ForCategory(tier + suffix) == wanted)
                {
                    return tier + suffix;
                }
            }
        }

        // A family with no tier list at all (the fallback) is reached through the hashed path.
        for (var seed = 0; seed < 10000; seed++)
        {
            var candidate = $"{family}-probe-{seed}";
            if (probe.ForCategory(candidate) == wanted)
            {
                return candidate;
            }
        }

        throw new InvalidOperationException($"no label reaches {family} slot {slot}");
    }

    /// <summary>The BASE colour of each slot of one family, in slot order.</summary>
    private static SKColor[] BaseColors(string family, bool isDark)
    {
        var bases = new List<SKColor>();
        foreach (var (key, color) in ChartPalette.CuratedColors(isDark))
        {
            // CuratedColors emits "<family>[n]" then "<family>[n] fast" for each slot in order.
            if (key.StartsWith($"{family}[", StringComparison.Ordinal) &&
                !key.EndsWith(" fast", StringComparison.Ordinal))
            {
                bases.Add(color);
            }
        }

        return [.. bases];
    }

    /// <summary>Each family's ramp as a flat colour set, for "is this colour one of theirs" checks.</summary>
    private static Dictionary<string, SKColor[]> RampsByFamily(bool isDark)
    {
        var ramps = new Dictionary<string, List<SKColor>>(StringComparer.Ordinal);
        foreach (var (key, color) in ChartPalette.CuratedColors(isDark))
        {
            var bracket = key.IndexOf('[');
            if (bracket < 0)
            {
                // The pooled "other", which belongs to no family.
                continue;
            }

            var family = key[..bracket];
            if (!ramps.TryGetValue(family, out var list))
            {
                ramps[family] = list = [];
            }

            list.Add(color);
        }

        var flattened = new Dictionary<string, SKColor[]>(StringComparer.Ordinal);
        foreach (var (family, list) in ramps)
        {
            flattened[family] = [.. list];
        }

        return flattened;
    }

    // ---------------------------------------------------------------- colour maths

    /// <summary>
    /// The dichromat simulations the palette is measured under.
    /// </summary>
    /// <remarks>
    /// Viénot, Brettel and Mollon (1999), "Digital video colourmaps for checking the legibility of
    /// displays by dichromats": one 3x3 matrix applied to LINEAR RGB, projecting a colour onto the
    /// plane the two remaining cone classes can span. Adequate here because it models the SEVERE
    /// case - a true dichromat, one cone class absent. Anomalous trichromats, the far commoner
    /// condition, see something between this and normal vision, so a palette that survives the
    /// simulation survives them too. Tritanopia is not simulated: the Viénot construction is
    /// documented as valid for protanopia and deuteranopia only, and tritanopia is roughly two
    /// orders of magnitude rarer.
    /// </remarks>
    private static readonly (string Name, Func<SKColor, SKColor> Simulate)[] Simulations =
    [
        ("deuteranopia", color => Project(color,
            [0.367322f, 0.860646f, -0.227968f, 0.280085f, 0.672501f, 0.047413f, -0.011820f, 0.042940f, 0.968881f])),
        ("protanopia", color => Project(color,
            [0.152286f, 1.052583f, -0.204868f, 0.114503f, 0.786281f, 0.099216f, -0.003882f, -0.048116f, 1.051998f])),
    ];

    private static SKColor Project(SKColor color, float[] m)
    {
        var r = Linear(color.Red);
        var g = Linear(color.Green);
        var b = Linear(color.Blue);
        return new SKColor(
            Encode((m[0] * r) + (m[1] * g) + (m[2] * b)),
            Encode((m[3] * r) + (m[4] * g) + (m[5] * b)),
            Encode((m[6] * r) + (m[7] * g) + (m[8] * b)));
    }

    private static double DeltaE(SKColor first, SKColor second)
    {
        var (l1, a1, b1) = Lab(first);
        var (l2, a2, b2) = Lab(second);
        return Math.Sqrt(((l1 - l2) * (l1 - l2)) + ((a1 - a2) * (a1 - a2)) + ((b1 - b2) * (b1 - b2)));
    }

    /// <summary>The shorter way round the Lab hue circle between two colours, in degrees.</summary>
    private static double HueGap(SKColor first, SKColor second)
    {
        var (_, a1, b1) = Lab(first);
        var (_, a2, b2) = Lab(second);
        var gap = Math.Abs((Math.Atan2(b1, a1) - Math.Atan2(b2, a2)) * 180.0 / Math.PI);
        return gap > 180.0 ? 360.0 - gap : gap;
    }

    /// <summary>sRGB to CIE Lab, D65 (the sRGB reference white), via the standard XYZ matrix.</summary>
    private static (double L, double A, double B) Lab(SKColor color)
    {
        double r = Linear(color.Red), g = Linear(color.Green), b = Linear(color.Blue);
        var x = ((0.4124564 * r) + (0.3575761 * g) + (0.1804375 * b)) / 0.95047;
        var y = (0.2126729 * r) + (0.7151522 * g) + (0.0721750 * b);
        var z = ((0.0193339 * r) + (0.1191920 * g) + (0.9503041 * b)) / 1.08883;

        static double F(double t) => t > 216.0 / 24389.0 ? Math.Cbrt(t) : ((841.0 / 108.0) * t) + (4.0 / 29.0);

        double fx = F(x), fy = F(y), fz = F(z);
        return ((116.0 * fy) - 16.0, 500.0 * (fx - fy), 200.0 * (fy - fz));
    }

    private static double Linear(byte channel)
    {
        var value = channel / 255.0;
        return value <= 0.04045 ? value / 12.92 : Math.Pow((value + 0.055) / 1.055, 2.4);
    }

    private static byte Encode(double linear)
    {
        var clamped = Math.Clamp(linear, 0.0, 1.0);
        var encoded = clamped <= 0.0031308 ? 12.92 * clamped : (1.055 * Math.Pow(clamped, 1.0 / 2.4)) - 0.055;
        return (byte)Math.Clamp(Math.Round(encoded * 255.0), 0.0, 255.0);
    }
}
