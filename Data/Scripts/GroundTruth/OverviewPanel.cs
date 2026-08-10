using System;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRageMath;

namespace GroundTruth
{
    // One panel, every instrument.
    //
    // The per-role apps suit small panels. A large-grid LCD is 2.5 m tall and a single
    // reading looks lost on it, so this shows all four systems at once - the layout the
    // Workshop splash image promises.
    //
    // THREE THINGS THIS APP DOES DIFFERENTLY:
    //
    // 1. It reads several instruments, so it opts out of the single-instrument lookup
    //    and uses StateForRole instead.
    //
    // 2. A missing instrument is a NORMAL state, not an error. That cell dims and says
    //    which block would fill it - which is honest, and doubles as an advert.
    //
    // 3. The layout branches on aspect ratio. SE surfaces range from roughly square
    //    (LCD panel, cockpit screens) to extremely wide (corner LCDs, some consoles),
    //    and a 2x2 grid on an 8:1 strip is unreadable.
    //
    // CONDENSE, DO NOT SHRINK. Each cell shows the headline only - the one number a
    // player wants at a glance. A quarter-size copy of a full panel reads as noise.
    [MyTextSurfaceScript("GT_Overview", "Ground Truth: Overview")]
    public class TssOverview : TssBase
    {
        public TssOverview(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        // Not tied to one role: this app is the whole suite.
        protected override float WantedRole { get { return 0f; } }
        protected override string Title { get { return "GROUND TRUTH"; } }
        protected override bool RequiresInstrument { get { return false; } }

        // Never called - RequiresInstrument is false, so DrawStandalone runs instead.
        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg) { }

        private struct Cell
        {
            public string Label;
            public string Value;
            public string Note;
            public Color Accent;
            public bool Present;
            public string Missing;
        }

        protected override void DrawStandalone(MySpriteDrawFrame frame, Color fg)
        {
            var cells = new Cell[4];
            cells[0] = Weather(fg);
            cells[1] = Radiation(fg);
            cells[2] = Habitat(fg);
            cells[3] = Life(fg);

            float top = 76f;
            float bottom = Canvas - 12f;
            float left = Pad;
            float right = CanvasWidth - Pad;

            // Wider than about 2.2:1 - corner LCDs, wide consoles - gets one row of four.
            // A 2x2 grid there leaves each cell a couple of hundred units wide and half
            // the panel empty.
            if (Aspect >= 2.2f)
            {
                float w = (right - left) / 4f;
                for (int i = 0; i < 4; i++)
                    DrawCell(frame, cells[i], left + i * w, top, w, bottom - top, fg, true);
                return;
            }

            // Taller than wide - some cockpit and console surfaces - stacks four rows.
            if (Aspect <= 0.75f)
            {
                float h = (bottom - top) / 4f;
                for (int i = 0; i < 4; i++)
                    DrawCell(frame, cells[i], left, top + i * h, right - left, h, fg, false);
                return;
            }

            // Everything else: the 2x2 the splash image shows.
            float cw = (right - left) / 2f;
            float ch = (bottom - top) / 2f;
            for (int i = 0; i < 4; i++)
                DrawCell(frame, cells[i],
                         left + (i % 2) * cw, top + (i / 2) * ch, cw, ch, fg, false);
        }

        // One cell. Sizes are derived from the cell rather than fixed, so the same code
        // serves a quarter of a 2.5 m panel and a quarter of a cockpit screen.
        private void DrawCell(MySpriteDrawFrame frame, Cell c,
                              float x, float y, float w, float h, Color fg, bool narrow)
        {
            float labelScale = narrow ? 0.5f : 0.6f;

            // Scale to the cell in BOTH axes. Height alone is not enough: a square panel
            // gives a cell about 234 units wide, and "ThunderstormHeavy" at scale 1.6
            // needs roughly 580. Weather names are the longest strings this panel shows
            // and they are not ours to shorten - they are the game's own effect names.
            //
            // ~20 canvas units per character at scale 1.0, measured against the Debug
            // font used throughout.
            float byHeight = h / 90f;
            float byWidth = string.IsNullOrEmpty(c.Value)
                ? 99f
                : (w - 12f) / (c.Value.Length * 20f);

            float valueScale = Math.Max(0.5f,
                Math.Min(narrow ? 1.1f : 1.6f, Math.Min(byHeight, byWidth)));

            Text(frame, c.Label, x + 6f, y, labelScale, fg * 0.5f, TextAlignment.LEFT);

            if (!c.Present)
            {
                // Dimmed, and it says what to build. A blank quadrant reads as broken;
                // this reads as incomplete, which is the truth.
                Text(frame, "--", x + 6f, y + 26f, valueScale, fg * 0.25f, TextAlignment.LEFT);
                Text(frame, c.Missing, x + 6f, y + 26f + valueScale * 34f, 0.45f,
                     fg * 0.3f, TextAlignment.LEFT);
                return;
            }

            Text(frame, c.Value, x + 6f, y + 26f, valueScale, c.Accent, TextAlignment.LEFT);

            if (!string.IsNullOrEmpty(c.Note))
                Text(frame, c.Note, x + 6f, y + 26f + valueScale * 34f, 0.5f,
                     fg * 0.55f, TextAlignment.LEFT);
        }

        // ---- the four headlines ----
        //
        // Each is the ONE thing a player wants at a glance. Detail stays on the
        // dedicated apps; this is the panel you look at while walking past.

        private Cell Weather(Color fg)
        {
            var s = StateForRole(Instruments.RoleWeather);
            var c = new Cell { Label = "WEATHER", Missing = "Weather Station", Accent = fg };
            if (s == null) return c;

            c.Present = true;

            if (!s.BodyHasWeather)
            {
                c.Value = "NONE";
                c.Note = "no weather system";
                c.Accent = fg * 0.7f;
                return c;
            }

            bool active = !string.IsNullOrEmpty(s.Weather);
            c.Value = active ? s.Weather : "CLEAR";
            c.Accent = !active ? Ok
                     : (s.Effect.HasHealth || (s.Effect.HasRadiation && s.Effect.RadiationGain > 0)
                        ? Danger : Caution);

            if (active)
            {
                double clears = s.SecondsToClear();
                c.Note = clears >= 0
                    ? string.Format("{0:P0}   clears {1}", s.WeatherIntensity, Clock(clears))
                    : string.Format("{0:P0}", s.WeatherIntensity);
            }
            return c;
        }

        private Cell Radiation(Color fg)
        {
            var s = StateForRole(Instruments.RoleRadiation);
            var c = new Cell { Label = "RADIATION", Missing = "Radiation Monitor", Accent = fg };
            if (s == null) return c;

            c.Present = true;

            if (!s.Rad.Enabled || s.Rad.IntensitySetting <= 0)
            {
                c.Value = "OFF";
                c.Note = "disabled in this world";
                c.Accent = fg * 0.7f;
                return c;
            }

            if (!s.Rad.Accumulates)
            {
                c.Value = "SAFE";
                c.Note = ShelterWord(s.Rad.ShelterState);
                c.Accent = Ok;
                return c;
            }

            // Accumulating: the headline is how long you have, not the rate.
            c.Value = Clock(s.Rad.SecondsToCritical);
            c.Note = "to critical";
            c.Accent = s.Rad.SecondsToCritical < 120 ? Danger
                     : (s.Rad.SecondsToCritical < 300 ? Caution : Solar);
            return c;
        }

        private Cell Habitat(Color fg)
        {
            var s = StateForRole(Instruments.RoleHabitat);
            var c = new Cell { Label = "HABITAT", Missing = "Habitat Monitor", Accent = fg };
            if (s == null) return c;

            c.Present = true;

            if (s.Airtight)
            {
                c.Value = "SEALED";
                c.Note = Clock(s.SealedSeconds);
                c.Accent = Ok;
            }
            else
            {
                c.Value = s.Breached ? "BREACH" : "OPEN";
                c.Note = s.Breached ? "seal lost" : "no sealed volume";
                c.Accent = Danger;
            }
            return c;
        }

        private Cell Life(Color fg)
        {
            var s = StateForRole(Instruments.RoleBio);
            var c = new Cell { Label = "LIFE", Missing = "Bio Systems Scanner", Accent = fg };
            if (s == null) return c;

            c.Present = true;

            if (!s.Bio.Valid)
            {
                c.Value = "--";
                c.Note = "scanning";
                c.Accent = fg * 0.6f;
                return c;
            }

            c.Value = s.Bio.Count.ToString();

            // Contacts outrank wildlife: a robot in range is the reason to look at this
            // panel, and it is never folded into the organism count.
            if (s.Bio.Contacts > 0)
            {
                c.Note = string.Format("+{0} contact{1}", s.Bio.Contacts,
                                       s.Bio.Contacts == 1 ? "" : "s");
                c.Accent = Danger;
            }
            else
            {
                c.Note = "biosignatures";
                c.Accent = s.Bio.Count > 0 ? Ok : fg * 0.7f;
            }
            return c;
        }

        private static string ShelterWord(int state)
        {
            switch (state)
            {
                case 3: return "atmosphere";
                case 2: return "sealed";
                case 1: return "occluded";
                default: return "exposed";
            }
        }
    }
}
