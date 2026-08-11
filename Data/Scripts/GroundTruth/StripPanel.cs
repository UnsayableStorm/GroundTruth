using System;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace GroundTruth
{
    // The corner LCD app.
    //
    // Corner LCDs are a band of screen a few centimetres tall and a metre or more wide.
    // Every other app in this mod - including Overview, which does branch on aspect -
    // assumes room for a title, a rule and a label above each number. On a strip that
    // furniture eats the only dimension in short supply, and what survives is correct
    // and unreadable from two metres away.
    //
    // TWO ROWS, NOT ONE
    //
    // The first version put one big line across the strip and nothing else, which left
    // every reading dependent on the viewer already knowing the model. "OUTSIDE 2:29"
    // is a true and useless thing to tell someone: 2:29 of what?
    //
    // There is height for a small context line above each value, so:
    //
    //     OUTSIDE, TO CRITICAL DOSE      <- context, small, dimmed
    //     2:29                           <- value, large, coloured
    //
    // The label says what is measured, the value says the state, the colour says how
    // much to care. None of the three has to do another's job, and the number stops
    // needing to be self-explanatory.
    //
    // NORMAL: one column per instrument, fixed order, so the strip does not reshuffle
    // as conditions change. A display that moves is one you read rather than glance at.
    // Severity lives in the colour, so a storm or a dose outside is visible without
    // displacing anything else.
    //
    // BREACH: the single exception, and the only state that clears the strip and
    // blinks. Everything else here describes the world; a breach describes the room the
    // reader is standing in.
    //
    // No title bar and no rule: DrawsChrome is false, a hook on TssBase added for
    // exactly this. The first attempt overrode Run and tried to reach past TssBase to
    // MyTSSCommon, which C# does not allow.
    [MyTextSurfaceScript("GT_Strip", "Ground Truth: Strip (corner LCD)")]
    public class TssStrip : TssBase
    {
        public TssStrip(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return 0f; } }
        protected override string Title { get { return "GROUND TRUTH"; } }
        protected override bool RequiresInstrument { get { return false; } }
        protected override bool DrawsChrome { get { return false; } }

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg) { }

        // Roughly how wide and tall the Debug font is per unit of scale, measured
        // against the other panels in this mod.
        private const float CharWidth = 20f;
        private const float LineHeight = 34f;

        // Shares of the strip's height. The value row dominates; the context row is
        // small enough to read as an annotation rather than as competing information.
        private const float ValueBand = 0.38f;
        private const float ContextBand = 0.19f;
        private const float GapBand = 0.05f;

        // A column: what is measured, what it says, and how much to care.
        private struct Entry
        {
            public string Label;
            public string Value;
            public Color Color;
            public Entry(string label, string value, Color color)
            {
                Label = label; Value = value; Color = color;
            }
        }

        protected override void DrawStandalone(MySpriteDrawFrame frame, Color fg)
        {
            var entries = Build(fg);

            float valueScale = (Canvas * ValueBand) / LineHeight;
            float contextScale = (Canvas * ContextBand) / LineHeight;

            // What each column needs is set by whichever of its two lines is wider - the
            // label is smaller but usually longer.
            var widths = new float[entries.Count];
            float total = 0f;
            for (int i = 0; i < entries.Count; i++)
            {
                float v = entries[i].Value.Length * CharWidth * valueScale;
                float l = entries[i].Label.Length * CharWidth * contextScale;
                widths[i] = Math.Max(v, l);
                total += widths[i];
            }

            float gap = CharWidth * valueScale * 1.5f;
            total += gap * Math.Max(0, entries.Count - 1);

            // Too wide: shrink both rows by the same factor so their relationship holds.
            float available = CanvasWidth - Pad * 2f;
            if (total > available && total > 0f)
            {
                float k = available / total;
                valueScale *= k;
                contextScale *= k;
                gap *= k;
                total = 0f;
                for (int i = 0; i < entries.Count; i++)
                {
                    widths[i] *= k;
                    total += widths[i];
                }
                total += gap * Math.Max(0, entries.Count - 1);
            }

            float blockHeight = (ContextBand + GapBand + ValueBand) * Canvas;
            float top = (Canvas - blockHeight) * 0.5f;
            float contextY = top;
            float valueY = top + (ContextBand + GapBand) * Canvas;

            float x = (CanvasWidth - total) * 0.5f;
            for (int i = 0; i < entries.Count; i++)
            {
                float mid = x + widths[i] * 0.5f;

                // The label is dimmed deliberately. It is there when you look for it and
                // does not compete with the number when you glance.
                Text(frame, entries[i].Label, mid, contextY, contextScale,
                     entries[i].Color * 0.55f, TextAlignment.CENTER);

                Text(frame, entries[i].Value, mid, valueY, valueScale,
                     entries[i].Color, TextAlignment.CENTER);

                x += widths[i] + gap;
            }
        }

        // ---- what to show ----

        private List<Entry> Build(Color fg)
        {
            var list = new List<Entry>();

            Entry alarm;
            if (Alarm(out alarm)) { list.Add(alarm); return list; }

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null) list.Add(WeatherEntry(wx, fg));

            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null) list.Add(RadEntry(rad, fg));

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null) list.Add(HabEntry(hab, fg));

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null) list.Add(BioEntry(bio, fg));

            if (list.Count == 0)
                list.Add(new Entry("GROUND TRUTH", "NO INSTRUMENTS", fg * 0.5f));

            return list;
        }

        // ---- the one alarm that takes the whole strip ----
        //
        // ONLY a pressure breach. Everything else - dose outside the hull, a storm, a
        // squad of bots on the horizon - is information about the world and belongs in
        // its column, coloured according to how much it matters.
        //
        // The first version promoted any of those to a full-width takeover, and the
        // result was a strip permanently showing the radiation outside a sealed ship in
        // space, with the other three instruments invisible behind it. An alarm that is
        // always on is not an alarm, it is a nameplate.
        //
        // A breach is different in kind: it is happening to the person reading the
        // screen, it is happening now, and there is nothing else they should be looking
        // at. So it clears the strip and blinks.
        private bool Alarm(out Entry entry)
        {
            entry = new Entry();

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab == null || hab.Airtight || !hab.Breached) return false;

            // Half a second on, half a second off, driven by the session frame counter
            // so every strip in the world blinks together. Dimmed rather than blanked:
            // a display that empties reads as a fault, and this one is reporting a fault
            // of its own.
            bool on = (GroundTruthSession.Frames / 30) % 2 == 0;
            entry = new Entry("HABITAT PRESSURE", "SEAL BREACHED",
                              on ? Danger : Danger * 0.28f);
            return true;
        }

        private static bool Hazardous(GroundTruthSession.BlockState s)
        {
            return s.Effect.HasHealth || (s.Effect.HasRadiation && s.Effect.RadiationGain > 0);
        }

        // ---- one column per instrument ----

        // Coloured and labelled by what the effect DOES rather than what it is called,
        // so a storm from a planet mod nobody has heard of still lands somewhere
        // sensible and still explains itself.
        private Entry WeatherEntry(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.BodyHasWeather) return new Entry("WEATHER", "NONE", fg * 0.4f);
            if (string.IsNullOrEmpty(s.Weather)) return new Entry("WEATHER", "CLEAR", Ok);

            string name = s.Weather.ToUpperInvariant();

            if (s.Effect.HasHealth) return new Entry("WEATHER, HARMFUL", name, Danger);
            if (s.Effect.HasRadiation && s.Effect.RadiationGain > 0)
                return new Entry("WEATHER, RADIOACTIVE", name, Cosmic);
            if (s.Effect.Oxygen < 0.999f) return new Entry("WEATHER, THINS THE AIR", name, Oxy);
            if (s.Effect.RadiationGain < 0) return new Entry("WEATHER, SHELTERS YOU", name, Wind);
            if (s.Effect.Solar < 0.9f) return new Entry("WEATHER, DIMS THE SUN", name, Solar);
            return new Entry("WEATHER", name, Caution);
        }

        private Entry RadEntry(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Rad.Enabled || s.Rad.IntensitySetting <= 0)
                return new Entry("RADIATION", "OFF", fg * 0.4f);

            if (!s.Rad.Accumulates)
                return new Entry(s.Airtight ? "RADIATION HERE" : "RADIATION OUTSIDE", "SAFE", Ok);

            double left = s.Rad.SecondsToCritical;
            return s.Airtight
                ? new Entry("RADIATION INSIDE THE SEAL", left >= 0 ? Clock(left) : "RISING",
                            left >= 0 && left < 300 ? Danger : Caution)
                : new Entry("OUTSIDE, TO CRITICAL DOSE", left >= 0 ? Clock(left) : "RISING",
                            Caution);
        }

        private Entry HabEntry(GroundTruthSession.BlockState s, Color fg)
        {
            if (s.Airtight) return new Entry("HABITAT PRESSURE", "SEALED", Ok);
            return new Entry("HABITAT PRESSURE", s.Breached ? "BREACH" : "OPEN",
                             s.Breached ? Danger : Caution);
        }

        private Entry BioEntry(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Bio.Valid) return new Entry("LIFE DETECTION", "--", fg * 0.4f);

            if (s.Bio.Contacts > 0)
                return new Entry("LIFE, " + s.Bio.Contacts + " NOT WILDLIFE",
                                 s.Bio.Count.ToString(), Caution);

            return new Entry("LIFE DETECTION", s.Bio.Count.ToString(),
                             s.Bio.Count > 0 ? Ok : Ok * 0.7f);
        }
    }
}
