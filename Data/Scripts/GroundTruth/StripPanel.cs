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
    // ALARM STATE: one column, filling the strip. While a seal is open nothing else
    // matters, and a row of reassuring green beside it would be worse than useless.
    //
    // ALL CLEAR: one column per instrument, fixed order, so the strip does not reshuffle
    // as conditions change. A display that moves is one you read rather than glance at.
    //
    // Priority among alarms is by how fast the thing kills you: vacuum, then dose, then
    // storm, then whatever is walking towards you.
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

        // ---- alarms, ordered by how fast the thing kills you ----

        private bool Alarm(out Entry entry)
        {
            entry = new Entry();

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null && !hab.Airtight && hab.Breached)
            {
                entry = new Entry("HABITAT PRESSURE", "SEAL BREACHED", Danger);
                return true;
            }

            // WHOSE dose is this?
            //
            // An instrument measures at ITS position, and a radiation monitor lives on
            // the hull, so it reports the vacuum rather than the room the screen is in.
            // The context line is what makes that survivable: the number is a limit on
            // going outside, and now says so.
            //
            // A monitor INSIDE a sealed volume that is still accumulating is the rare
            // and genuinely alarming case - the seal is not protecting anyone - and is
            // the only radiation state allowed to turn the strip red.
            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null && rad.Rad.Enabled && rad.Rad.Accumulates)
            {
                double left = rad.Rad.SecondsToCritical;
                string value = left >= 0 ? Clock(left) : "RISING";

                entry = rad.Airtight
                    ? new Entry("RADIATION INSIDE THE SEAL", value,
                                left >= 0 && left < 300 ? Danger : Caution)
                    : new Entry("OUTSIDE, TO CRITICAL DOSE", value, Caution);
                return true;
            }

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null && !string.IsNullOrEmpty(wx.Weather) && Hazardous(wx))
            {
                entry = new Entry("WEATHER, HARMFUL", wx.Weather.ToUpperInvariant(), Danger);
                return true;
            }

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null && bio.Bio.Valid && bio.Bio.Contacts > 0)
            {
                entry = new Entry("CONTACTS, NOT WILDLIFE",
                                  bio.Bio.Contacts.ToString(), Caution);
                return true;
            }

            return false;
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
