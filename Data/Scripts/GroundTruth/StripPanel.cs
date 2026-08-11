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
    // assumes enough height for a title, a rule and a label above each number. On a
    // strip that furniture eats the only dimension in short supply, and what survives is
    // technically correct and unreadable from two metres away.
    //
    // So this app is not a squeezed Overview. It answers ONE question, in the largest
    // text that fits:
    //
    //     is anything wrong?
    //
    // ALARM STATE: the single worst condition, filling the strip, in its own colour.
    // Nothing else is shown, because nothing else matters while a seal is open.
    //
    // ALL CLEAR: a compact line of four tokens - weather, radiation, seal, life - so a
    // glance confirms the instruments are live rather than merely silent.
    //
    // Priority when several things are wrong at once is by how fast it kills you:
    // vacuum, then dose, then storm, then whatever is walking towards you.
    //
    // No title bar, no rule, no per-cell labels. DrawsChrome is false, which is a hook
    // on TssBase added for exactly this - the first attempt overrode Run and tried to
    // reach past TssBase to MyTSSCommon, which C# does not allow and which would have
    // duplicated the base class housekeeping to boot.
    [MyTextSurfaceScript("GT_Strip", "Ground Truth: Strip (corner LCD)")]
    public class TssStrip : TssBase
    {
        public TssStrip(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return 0f; } }
        protected override string Title { get { return "GROUND TRUTH"; } }
        protected override bool RequiresInstrument { get { return false; } }

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg) { }

        // Roughly how wide and tall the Debug font is per unit of scale, measured
        // against the other panels in this mod.
        private const float CharWidth = 20f;
        private const float LineHeight = 34f;

        protected override bool DrawsChrome { get { return false; } }

        // A token is a word plus the colour that carries its meaning. The word names
        // the SYSTEM, the colour reports its STATE - so "WEATHER" in green says more
        // than "CLEAR" ever did, and needs no legend.
        private struct Token
        {
            public string Text;
            public Color Color;
            public Token(string text, Color color) { Text = text; Color = color; }
        }

        protected override void DrawStandalone(MySpriteDrawFrame frame, Color fg)
        {
            var tokens = Build(fg);

            // Width in canvas units at scale 1, including a gap of one and a half
            // characters between tokens.
            float chars = 0f;
            for (int i = 0; i < tokens.Count; i++) chars += tokens[i].Text.Length;
            chars += 1.5f * Math.Max(0, tokens.Count - 1);

            float byHeight = (Canvas * 0.62f) / LineHeight;
            float byWidth = (CanvasWidth - Pad * 2f) / Math.Max(1f, chars * CharWidth);
            float scale = Math.Max(0.6f, Math.Min(byHeight, byWidth));

            float total = chars * CharWidth * scale;
            float x = (CanvasWidth - total) * 0.5f;
            float y = (Canvas - LineHeight * scale) * 0.5f;
            float gap = 1.5f * CharWidth * scale;

            for (int i = 0; i < tokens.Count; i++)
            {
                Text(frame, tokens[i].Text, x, y, scale, tokens[i].Color, TextAlignment.LEFT);
                x += tokens[i].Text.Length * CharWidth * scale + gap;
            }
        }

        // ---- what to show ----
        //
        // One alarm fills the strip alone: while a seal is open, nothing else matters
        // and a row of reassuring green beside it would be worse than useless.
        //
        // Otherwise every present instrument contributes one token. Order is fixed so
        // the strip does not reshuffle as conditions change - a display that moves is a
        // display you have to read rather than glance at.
        private List<Token> Build(Color fg)
        {
            var list = new List<Token>();

            Token alarm;
            if (Alarm(out alarm)) { list.Add(alarm); return list; }

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null) list.Add(WeatherToken(wx, fg));

            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null) list.Add(RadToken(rad, fg));

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null) list.Add(HabToken(hab, fg));

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null) list.Add(BioToken(bio, fg));

            if (list.Count == 0) list.Add(new Token("NO INSTRUMENTS", fg * 0.5f));
            return list;
        }

        // ---- alarms, ordered by how fast the thing kills you ----

        private bool Alarm(out Token token)
        {
            token = new Token();

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null && !hab.Airtight && hab.Breached)
            {
                token = new Token("SEAL BREACHED", Danger);
                return true;
            }

            // WHOSE dose is this?
            //
            // An instrument measures at ITS position. A monitor bolted to the hull of a
            // sealed ship in space reads full solar exposure and is telling the truth -
            // about the hull. Presenting that as RADIATION 02:29 on a screen inside the
            // ship reads as a countdown for the person looking at it, which is a lie by
            // framing even though every number is correct.
            //
            // So the strip says which side of the wall the reading came from. A monitor
            // sitting in a sealed volume speaks for the room and shouts; one out in the
            // open speaks for the outside, is worth knowing before an EVA, and does not
            // get to turn the panel red at someone who is safely indoors.
            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null && rad.Rad.Enabled && rad.Rad.Accumulates)
            {
                double left = rad.Rad.SecondsToCritical;
                bool indoors = rad.Airtight;

                // OUTSIDE for the hull-mounted case, which is where a radiation monitor
                // almost always lives. It is a fact about the vacuum, not a countdown
                // for whoever is reading the screen, so it stays amber - useful before
                // an EVA, not a reason to panic in a pressurised cabin.
                //
                // A monitor that is INSIDE a sealed volume and still accumulating is the
                // rare and genuinely alarming case: the seal is not protecting you. That
                // one gets the bare word and the red.
                token = indoors
                    ? new Token(left >= 0 ? "RADIATION  " + Clock(left) : "RADIATION",
                                left >= 0 && left < 300 ? Danger : Caution)
                    : new Token(left >= 0 ? "OUTSIDE  " + Clock(left) : "OUTSIDE", Caution);
                return true;
            }

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null && !string.IsNullOrEmpty(wx.Weather) && Hazardous(wx))
            {
                token = new Token(wx.Weather.ToUpperInvariant(), Danger);
                return true;
            }

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null && bio.Bio.Valid && bio.Bio.Contacts > 0)
            {
                token = new Token(bio.Bio.Contacts == 1 ? "CONTACT"
                                  : bio.Bio.Contacts + " CONTACTS", Caution);
                return true;
            }

            return false;
        }

        private static bool Hazardous(GroundTruthSession.BlockState s)
        {
            return s.Effect.HasHealth || (s.Effect.HasRadiation && s.Effect.RadiationGain > 0);
        }

        // ---- one token per system ----

        // Clear weather is the word WEATHER in green: the system is named, the colour
        // says it is fine. When something IS happening the name of the effect is worth
        // the space, coloured by what that effect DOES rather than by what it is
        // called - so a modded storm nobody has heard of still lands on a sensible
        // colour.
        private Token WeatherToken(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.BodyHasWeather) return new Token("WEATHER", fg * 0.4f);
            if (string.IsNullOrEmpty(s.Weather)) return new Token("WEATHER", Ok);

            string name = s.Weather.ToUpperInvariant();

            if (s.Effect.HasHealth) return new Token(name, Danger);
            if (s.Effect.HasRadiation && s.Effect.RadiationGain > 0) return new Token(name, Cosmic);
            if (s.Effect.Oxygen < 0.999f) return new Token(name, Oxy);
            if (s.Effect.RadiationGain < 0) return new Token(name, Wind);   // rain: shelter
            if (s.Effect.Solar < 0.9f) return new Token(name, Solar);
            return new Token(name, Caution);
        }

        // RAD never changes its word. Green safe, amber accumulating, red running out,
        // dim when the world has radiation switched off entirely.
        private Token RadToken(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Rad.Enabled || s.Rad.IntensitySetting <= 0) return new Token("RAD", fg * 0.4f);
            if (!s.Rad.Accumulates) return new Token("RAD", Ok);

            // In the all-clear row the word stays RAD and the colour does the work:
            // amber for a hull sensor in the open, red only when a sealed volume is
            // failing to protect whoever is inside it.
            double left = s.Rad.SecondsToCritical;
            if (!s.Airtight) return new Token("RAD", Caution);
            return new Token("RAD", left >= 0 && left < 300 ? Danger : Caution);
        }

        // The word changes here because SEALED and OPEN are different facts, not
        // different severities of one fact. The colour still does the shouting.
        private Token HabToken(GroundTruthSession.BlockState s, Color fg)
        {
            if (s.Airtight) return new Token("SEALED", Ok);
            return new Token(s.Breached ? "BREACH" : "OPEN", s.Breached ? Danger : Caution);
        }

        // Life is a count, so the number has to be there. Green while it is only
        // wildlife; contacts promote the whole token to amber, and are the one thing
        // here that also raises an alarm on its own.
        private Token BioToken(GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Bio.Valid) return new Token("LIFE --", fg * 0.4f);
            if (s.Bio.Contacts > 0) return new Token("LIFE " + s.Bio.Count, Caution);
            return new Token("LIFE " + s.Bio.Count, s.Bio.Count > 0 ? Ok : Ok * 0.7f);
        }
    }
}
