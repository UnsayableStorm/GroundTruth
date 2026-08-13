using System;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;

namespace GroundTruth
{
    // LCD apps.
    //
    // Registered as Text Surface Scripts, so they appear in the panel's own script
    // dropdown alongside the vanilla ones. No name tags, no config, no pairing.
    //
    // The panel is not an instrument, so each app finds one on its own grid and reads
    // the same cached state the detail pane and the terminal properties use. One
    // computation, three presentations - they cannot disagree.
    //
    // LAYOUT: everything is authored against a 512-tall virtual canvas and scaled to the
    // real surface. Absolute pixel positions looked correct on a small LCD and left
    // everything huddled in the top third of a cockpit screen.
    //
    // Colours come from the surface's own script colour settings, so a player who has
    // themed their cockpit keeps their theme.
    public abstract class TssBase : MyTSSCommon
    {
        protected const float Canvas = 512f;
        protected const float Pad = 22f;

        // Semantic palette. Chrome - titles, rules, labels - still follows the surface's
        // script colour so a themed cockpit stays themed. STATE does not: a reading that
        // means "you are dying" must not be the same colour as one that means "fine"
        // because the player picked a nice blue. Status colour is information, not style.
        protected static readonly Color Ok = new Color(90, 220, 130);
        protected static readonly Color Caution = new Color(255, 190, 70);
        protected static readonly Color Danger = new Color(255, 90, 80);
        protected static readonly Color Solar = new Color(255, 205, 90);
        protected static readonly Color Wind = new Color(120, 225, 225);
        protected static readonly Color Oxy = new Color(120, 180, 255);
        protected static readonly Color Cosmic = new Color(190, 130, 255);

        protected readonly IMyTextSurface Surface;
        protected readonly IMyCubeBlock Block;

        private IMyTerminalBlock _instrument;
        private int _searchCooldown;

        /// <summary>The instrument this app is currently reading, or null.</summary>
        protected IMyTerminalBlock Instrument { get { return _instrument; } }

        // Unit conversion for the virtual canvas.
        protected float U;
        protected Vector2 Org;
        protected Vector2 Size;

        // Data refreshes once a second; there is nothing to gain from drawing faster.
        public override ScriptUpdate NeedsUpdate { get { return ScriptUpdate.Update100; } }

        protected TssBase(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size)
        {
            Surface = surface;
            Block = block;
        }

        protected abstract float WantedRole { get; }
        protected abstract string Title { get; }
        protected abstract void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg);

        // An app that reads SEVERAL instruments cannot use the single cached lookup, and
        // must not show "NO INSTRUMENT" just because one role is missing.
        protected virtual bool RequiresInstrument { get { return true; } }
        protected virtual void DrawStandalone(MySpriteDrawFrame frame, Color fg) { }

        /// <summary>
        /// Title and rule across the top. True everywhere except the corner LCD strip,
        /// where the header would consume the only dimension that is scarce.
        /// </summary>
        protected virtual bool DrawsChrome { get { return true; } }

        // Aspect ratio of the surface in canvas units. 1.0 is square; a corner LCD is
        // roughly 8, a cockpit screen closer to 1.3.
        protected float Aspect { get { return Size.X / Size.Y; } }
        protected float CanvasWidth { get { return Canvas * Aspect; } }

        public override void Run()
        {
            base.Run();

            Size = Surface.SurfaceSize;
            Org = (Surface.TextureSize - Size) * 0.5f;
            U = Size.Y / Canvas;

            var fg = Surface.ScriptForegroundColor;

            if (!RequiresInstrument)
            {
                using (var multi = Surface.DrawFrame())
                {
                    if (DrawsChrome)
                    {
                        Text(multi, Title, Pad, 16, 0.85f, fg, TextAlignment.LEFT);
                        Rule(multi, 58, fg * 0.9f, 2f);
                    }
                    DrawStandalone(multi, fg);
                }
                return;
            }

            var instrument = FindInstrument();
            var state = instrument == null ? null : GroundTruthSession.StateFor(instrument);

            using (var frame = Surface.DrawFrame())
            {
                Text(frame, Title, Pad, 16, 0.85f, fg, TextAlignment.LEFT);
                Rule(frame, 58, fg * 0.9f, 2f);

                if (state == null)
                {
                    Text(frame, "NO INSTRUMENT", Pad, 96, 1.5f, fg * 0.55f, TextAlignment.LEFT);
                    Text(frame, "On this grid:", Pad, 180, 0.7f, fg * 0.45f, TextAlignment.LEFT);

                    // Say what IS on the grid rather than only what is missing. A bare
                    // "not found" cannot distinguish an absent block from one the app
                    // failed to recognise, and those need different fixes.
                    float y = 214;
                    int found = 0;
                    foreach (var line in DescribeInstruments())
                    {
                        if (y > 460) break;
                        Text(frame, line, Pad, y, 0.65f, fg * 0.5f, TextAlignment.LEFT);
                        y += 28; found++;
                    }
                    if (found == 0)
                        Text(frame, "No Ground Truth blocks on this grid.", Pad, y, 0.65f, fg * 0.45f, TextAlignment.LEFT);
                    return;
                }

                Draw(frame, state, fg);
            }
        }

        // Grid walks are expensive, so the instrument is cached and only re-found when
        // the cached one has gone away - and then no more than once every few updates.
        private IMyTerminalBlock FindInstrument()
        {
            if (_instrument != null && !_instrument.Closed && _instrument.CubeGrid == Block.CubeGrid)
                return _instrument;

            if (_searchCooldown > 0) { _searchCooldown--; return null; }
            _searchCooldown = 3;

            var grid = Block.CubeGrid as IMyCubeGrid;
            if (grid == null) return null;

            var slims = new List<IMySlimBlock>();
            grid.GetBlocks(slims, sb => sb.FatBlock is IMyTerminalBlock);

            foreach (var sb in slims)
            {
                var tb = sb.FatBlock as IMyTerminalBlock;
                if (tb == null) continue;
                if (RoleOf(tb) != WantedRole) continue;
                if (GroundTruthSession.StateFor(tb) == null) continue;

                _instrument = tb;
                return tb;
            }
            return null;
        }

        private static float RoleOf(IMyTerminalBlock b)
        {
            return Instruments.RoleOf(b.BlockDefinition.SubtypeName);
        }


        // One instrument per role, cached with the same cooldown as the single lookup.
        // A grid usually has at most one of each, and a missing role is a normal state
        // rather than an error - the overview dims that quadrant instead of failing.
        private readonly Dictionary<float, IMyTerminalBlock> _byRole =
            new Dictionary<float, IMyTerminalBlock>();
        private int _roleCooldown;

        protected GroundTruthSession.BlockState StateForRole(float role)
        {
            IMyTerminalBlock cached;
            if (_byRole.TryGetValue(role, out cached) && cached != null && !cached.Closed
                && cached.CubeGrid == Block.CubeGrid)
                return GroundTruthSession.StateFor(cached);

            if (_roleCooldown > 0) { _roleCooldown--; return null; }
            _roleCooldown = 3;

            var grid = Block.CubeGrid as IMyCubeGrid;
            if (grid == null) return null;

            var slims = new List<IMySlimBlock>();
            grid.GetBlocks(slims, sb => sb.FatBlock is IMyTerminalBlock);

            _byRole.Clear();
            foreach (var sb in slims)
            {
                var tb = sb.FatBlock as IMyTerminalBlock;
                if (tb == null) continue;

                float r = Instruments.RoleOf(tb.BlockDefinition.SubtypeName);
                if (r <= 0 || _byRole.ContainsKey(r)) continue;
                if (GroundTruthSession.StateFor(tb) == null) continue;

                _byRole[r] = tb;
            }

            return _byRole.TryGetValue(role, out cached)
                ? GroundTruthSession.StateFor(cached) : null;
        }

        // Every Ground Truth block on this grid with the role the table gives it.
        private List<string> DescribeInstruments()
        {
            var lines = new List<string>();
            try
            {
                var grid = Block.CubeGrid as IMyCubeGrid;
                if (grid == null) return lines;

                var slims = new List<IMySlimBlock>();
                grid.GetBlocks(slims, sb => sb.FatBlock is IMyTerminalBlock);

                foreach (var sb in slims)
                {
                    var tb = sb.FatBlock as IMyTerminalBlock;
                    if (tb == null) continue;
                    var sub = tb.BlockDefinition.SubtypeName;
                    if (sub == null || !sub.StartsWith("GT_")) continue;
                    lines.Add(tb.DisplayNameText);
                }
            }
            catch { }
            return lines;
        }

        // Deadpan filler for the states where an instrument has nothing to say.
        //
        // Keyed to the INSTRUMENT's entity id, so two panels reading the same block agree
        // and two blocks differ. Deterministic rather than random because the panel
        // redraws every second and would otherwise flicker through the whole pool.
        //
        // These only ever appear where the honest reading is "nothing is happening".
        // Nothing that matters is ever replaced by a joke.
        protected string Remark(string[] pool)
        {
            if (pool == null || pool.Length == 0) return "";

            long id = Instrument != null ? Instrument.EntityId
                    : (Block == null ? 0 : Block.EntityId);

            int i = (int)(((id ^ (id >> 32)) & 0x7FFFFFFF) % pool.Length);
            return pool[i];
        }

        // ---- drawing, all in canvas units ----

        protected void Text(MySpriteDrawFrame frame, string text, float x, float y,
                            float scale, Color color, TextAlignment align)
        {
            var s = MySprite.CreateText(text, "Debug", color, scale * U, align);
            s.Position = Org + new Vector2(x * U, y * U);
            frame.Add(s);
        }

        protected void TextRight(MySpriteDrawFrame frame, string text, float y,
                                 float scale, Color color)
        {
            Text(frame, text, Canvas * (Size.X / Size.Y) - Pad, y, scale, color, TextAlignment.RIGHT);
        }

        protected void Rule(MySpriteDrawFrame frame, float y, Color color, float thickness)
        {
            var w = Size.X - Pad * 2f * U;
            var s = MySprite.CreateSprite("SquareSimple",
                new Vector2(Org.X + Pad * U + w * 0.5f, Org.Y + y * U),
                new Vector2(w, thickness * U));
            s.Color = color;
            frame.Add(s);
        }

        // Filled proportion of a track. Used for intensity, exposure and output.
        protected void Bar(MySpriteDrawFrame frame, float x, float y, float w, float h,
                           float fraction, Color track, Color fill)
        {
            fraction = MathHelper.Clamp(fraction, 0f, 1f);

            var px = Org.X + x * U;
            var py = Org.Y + y * U;
            var pw = w * U;
            var ph = h * U;

            var bg = MySprite.CreateSprite("SquareSimple", new Vector2(px + pw * 0.5f, py + ph * 0.5f), new Vector2(pw, ph));
            bg.Color = track;
            frame.Add(bg);

            if (fraction <= 0f) return;

            var fw = pw * fraction;
            var fs = MySprite.CreateSprite("SquareSimple", new Vector2(px + fw * 0.5f, py + ph * 0.5f), new Vector2(fw, ph));
            fs.Color = fill;
            frame.Add(fs);
        }

        // A species icon. The texture is already a finished unit - squircle plate with
        // the creature composited onto it - so this is one sprite that scales as a
        // whole. Drawn in white: the art is colour, and tinting it to the surface
        // theme would turn the animals grey.
        //
        // x,y is the TOP-LEFT in canvas units, matching the text helpers, even though
        // MySprite positions from the centre.
        protected void Icon(MySpriteDrawFrame frame, string sprite, float x, float y, float size)
        {
            var s = MySprite.CreateSprite(sprite,
                new Vector2(Org.X + (x + size * 0.5f) * U, Org.Y + (y + size * 0.5f) * U),
                new Vector2(size * U, size * U));
            s.Color = Color.White;
            frame.Add(s);
        }

        // Definition subtype to icon. Matching is loose and case-insensitive because
        // the same creature arrives as "Cow_Bot" from MES and "Wolf" from vanilla, and
        // modded fauna use their own conventions entirely.
        //
        // Anything unmatched gets the paw-print marker and KEEPS ITS RAW SUBTYPE TEXT
        // on the panel. Drawing an unrecognised organism as a cow would be the
        // instrument inventing a reading.
        protected static string IconFor(string subtype)
        {
            if (string.IsNullOrEmpty(subtype)) return "GT_Bio_Unknown";
            var s = subtype.ToLowerInvariant();

            if (s.Contains("cow") || s.Contains("bull") || s.Contains("cattle")) return "GT_Bio_Cow";
            if (s.Contains("horse")) return "GT_Bio_Horse";
            if (s.Contains("sheep") || s.Contains("lamb") || s.Contains("ram")) return "GT_Bio_Sheep";
            if (s.Contains("wolf") || s.Contains("dog")) return "GT_Bio_Wolf";
            if (s.Contains("spider")) return "GT_Bio_Spider";

            // Machines and armed humanoids, decided by the same rule the counts use.
            if (Readings.IsContact(subtype)) return "GT_Bio_Robot";

            return "GT_Bio_Unknown";
        }

        // Radial gauge. SE gives no arc primitive, so the arc is drawn as a run of small
        // rotated quads along the path - cheap, and it scales with the canvas like
        // everything else. Angles are degrees clockwise from straight up.
        //
        // A dial reads faster than a bar for "how close to the limit am I", which is the
        // question every one of these panels is really answering.
        protected void Arc(MySpriteDrawFrame frame, float cx, float cy, float radius,
                           float thickness, float startDeg, float sweepDeg,
                           float fraction, Color track, Color fill, int segments)
        {
            fraction = MathHelper.Clamp(fraction, 0f, 1f);

            // Overlap slightly so the segments read as a continuous band, not a dashed one.
            float segLen = (float)(2.0 * Math.PI * radius * (sweepDeg / 360.0) / segments) * 1.35f;
            int lit = (int)Math.Round(segments * fraction);

            for (int i = 0; i < segments; i++)
            {
                float a = startDeg + sweepDeg * ((i + 0.5f) / segments);
                double rad = a * Math.PI / 180.0;
                float px = cx + radius * (float)Math.Sin(rad);
                float py = cy - radius * (float)Math.Cos(rad);

                var s = MySprite.CreateSprite("SquareSimple",
                    new Vector2(Org.X + px * U, Org.Y + py * U),
                    new Vector2(thickness * U, segLen * U));
                s.Color = i < lit ? fill : track;
                s.RotationOrScale = (float)rad;
                frame.Add(s);
            }
        }

        // A single mark on an arc - a threshold, a limit, a reference value.
        protected void ArcMark(MySpriteDrawFrame frame, float cx, float cy, float radius,
                               float thickness, float length, float deg, Color color)
        {
            double rad = deg * Math.PI / 180.0;
            float px = cx + radius * (float)Math.Sin(rad);
            float py = cy - radius * (float)Math.Cos(rad);
            var s = MySprite.CreateSprite("SquareSimple",
                new Vector2(Org.X + px * U, Org.Y + py * U),
                new Vector2(length * U, thickness * U));
            s.Color = color;
            s.RotationOrScale = (float)rad;
            frame.Add(s);
        }

        // Value centred inside a gauge, with its unit underneath.
        // Distance from the dial centre to the caption under it. Set by the callers'
        // ring radius; 108 clears an 84-radius ring with its 20-thick band.
        protected const float SubLabelDrop = 108f;

        protected void GaugeLabel(MySpriteDrawFrame frame, float cx, float cy,
                                  string value, string unit, float scale, Color color, Color dim)
        {
            var s = MySprite.CreateText(value, "Debug", color, scale * U, TextAlignment.CENTER);
            s.Position = Org + new Vector2(cx * U, (cy - scale * 22f) * U);
            frame.Add(s);

            if (string.IsNullOrEmpty(unit)) return;

            // Below the ring, not inside it. Inside, anything longer than a word or two
            // runs straight through the arc - "NOT ACCUMULATING" did exactly that.
            var u = MySprite.CreateText(unit, "Debug", dim, 0.6f * U, TextAlignment.CENTER);
            u.Position = Org + new Vector2(cx * U, (cy + SubLabelDrop) * U);
            frame.Add(u);
        }

        // Width of the content area in canvas units, accounting for wide panels.
        protected float ContentWidth { get { return (Size.X / U) - Pad * 2f; } }

        protected static string Clock(double seconds)
        {
            if (seconds < 0) return "--:--";
            var t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1
                ? string.Format("{0}:{1:00}:{2:00}", (int)t.TotalHours, t.Minutes, t.Seconds)
                : string.Format("{0:00}:{1:00}", t.Minutes, t.Seconds);
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Radiation", "Ground Truth: Radiation")]
    public class TssRadiation : TssBase
    {
        public TssRadiation(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return TerminalApi.RoleRadiation; } }
        protected override string Title { get { return "RADIATION"; } }

        // The Radiation Monitor on an Earthlike has nothing to do, and says so in the
        // house voice. Only shown when the reading is genuinely nil AND the shielding is
        // total - a monitor that is merely below the accumulation threshold is still
        // reporting something a player should see.
        private static readonly string[] IdleRemarks =
        {
            "atmospheric shielding total; no further action indicated",
            "background nominal, monitoring continues regardless",
            "exposure zero; the instrument declines to speculate about elsewhere",
            "nothing to report, reported anyway",
            "shielding total; Survey and Assessment were not surprised",
            "on duty, unbothered",
        };

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Rad.Enabled || s.Rad.IntensitySetting <= 0)
            {
                Text(frame, "DISABLED", Pad, 96, 1.8f, fg * 0.5f, TextAlignment.LEFT);
                Text(frame, "Radiation is off in this world.", Pad, 210, 0.8f, fg * 0.45f, TextAlignment.LEFT);
                return;
            }

            bool hot = s.Rad.Accumulates;

            // The dial is the survivable time, emptying as it runs out, against a ten
            // minute reference. Colour carries the urgency, so state is legible before
            // any number is: green safe, amber under five minutes, red under two.
            float cx = 132f, cy = 212f, r = 84f;
            float remaining = hot
                ? MathHelper.Clamp((float)(s.Rad.SecondsToCritical / 600.0), 0f, 1f)
                : 1f;

            Color accent = !hot ? Ok
                         : (s.Rad.SecondsToCritical < 120 ? Danger
                         : (s.Rad.SecondsToCritical < 300 ? Caution : Solar));

            Arc(frame, cx, cy, r, 20f, -140f, 280f, remaining, fg * 0.15f, accent, 44);
            GaugeLabel(frame, cx, cy, hot ? Clock(s.Rad.SecondsToCritical) : "SAFE",
                       hot ? "TO CRITICAL" : "NOT ACCUMULATING", 1.2f, accent, fg * 0.55f);

            // Idle remark, only where there is genuinely nothing to say: no accumulation
            // and complete atmospheric shielding. Anywhere else the panel has real work.
            if (!hot && s.Rad.AtmosphericShielding >= 1.0)
                Text(frame, Remark(IdleRemarks), Pad, cy + SubLabelDrop + 26f, 0.45f,
                     fg * 0.35f, TextAlignment.LEFT);

            // Shelter state as four filled pips - a ladder, not a word to decode.
            // Shelter pips run red to green up the ladder, so the colour says how
            // protected you are before the word underneath is read.
            var pipColor = s.Rad.ShelterState == 0 ? Danger
                         : (s.Rad.ShelterState == 1 ? Caution : Ok);
            float px = 268f;
            for (int i = 0; i < 4; i++)
            {
                var pip = MySprite.CreateSprite("SquareSimple",
                    new Vector2(Org.X + (px + i * 26f) * U, Org.Y + 168f * U),
                    new Vector2(18f * U, 18f * U));
                pip.Color = i <= s.Rad.ShelterState ? pipColor : fg * 0.18f;
                frame.Add(pip);
            }

            string shelter;
            switch (s.Rad.ShelterState)
            {
                case 3: shelter = "ATMOSPHERE"; break;
                case 2: shelter = "SEALED"; break;
                case 1: shelter = "OCCLUDED"; break;
                default: shelter = "EXPOSED"; break;
            }
            Text(frame, "SHELTER", px, 122, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            Text(frame, shelter, px, 196, 0.85f, pipColor, TextAlignment.LEFT);

            Text(frame, "SHIELDING", px, 256, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            Bar(frame, px, 284, 150f, 12f, (float)s.Rad.AtmosphericShielding, fg * 0.15f,
                s.Rad.AtmosphericShielding >= 1.0 ? Ok : Caution);
            Text(frame, s.Rad.AtmosphericShielding >= 1.0 ? "TOTAL"
                     : string.Format("{0:P0}", s.Rad.AtmosphericShielding),
                 px, 318, 0.7f, fg * 0.85f, TextAlignment.LEFT);

            Text(frame, "EXPOSURE", px, 372, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            Text(frame, string.Format("{0:F4} /s", s.Rad.Total), px, 404, 0.8f,
                 hot ? accent : fg * 0.8f, TextAlignment.LEFT);

            // Source breakdown: three stacked bars against the same scale, so a reader
            // can see WHICH term is doing the damage rather than only the total.
            float scale = (float)Math.Max(s.Rad.Total, 0.0001);
            // One colour per source, constant across every panel and every reading, so
            // the eye learns which band is which instead of re-reading the labels.
            float by = 452f;
            DrawSource(frame, "SOL", (float)(s.Rad.Solar / scale), by, fg, Solar);
            DrawSource(frame, "PLA", (float)(s.Rad.Planetary / scale), by + 22f, fg, Cosmic);
            DrawSource(frame, "WX ", (float)(s.Rad.Weather / scale), by + 44f, fg, Oxy);
        }

        private void DrawSource(MySpriteDrawFrame frame, string label, float frac, float y, Color fg, Color accent)
        {
            Text(frame, label, Pad, y - 4f, 0.55f, fg * 0.5f, TextAlignment.LEFT);
            Bar(frame, Pad + 46f, y, ContentWidth - 46f, 10f,
                MathHelper.Clamp(frac, 0f, 1f), fg * 0.12f, frac > 0.001f ? accent : fg * 0.2f);
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Habitat", "Ground Truth: Habitat")]
    public class TssHabitat : TssBase
    {
        public TssHabitat(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return TerminalApi.RoleHabitat; } }
        protected override string Title { get { return "HABITAT"; } }

        // A seal that has held for a long time is the most boring reading in the pack.
        private static readonly string[] IdleRemarks =
        {
            "pressure nominal; duration logged",
            "seal holding; no action required or expected",
            "integrity maintained, as designed",
            "no breach. the instrument has never seen a breach here.",
            "conditions unremarkable, which is the preferred outcome",
            "stable. filed under stable.",
        };

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg)
        {
            var accent = s.Airtight ? Ok : Danger;
            float cx = 132f, cy = 212f, r = 84f;

            // A closed ring is the seal. It breaks open when the seal does - the shape
            // itself carries the state before any word is read.
            if (s.Airtight)
            {
                Arc(frame, cx, cy, r, 20f, 0f, 360f, 1f, fg * 0.15f, Ok, 56);
                GaugeLabel(frame, cx, cy, "SEALED", Clock(s.SealedSeconds), 1.1f, Ok, fg * 0.55f);

                // Only after five minutes of holding. A seal that was just established is
                // still news; one that has held all day is not.
                if (s.SealedSeconds > 300)
                    Text(frame, Remark(IdleRemarks), Pad, cy + SubLabelDrop + 26f, 0.45f,
                         fg * 0.35f, TextAlignment.LEFT);
            }
            else if (s.SealStatus == Readings.SealNoGasSystem
                     || s.SealStatus == Readings.SealAwaitingServer)
            {
                // NOT an alarm. Pressurisation is server-authoritative and a client can
                // be waiting on it - drawing a red OPEN there is a false negative with a
                // siren attached. Dim, dashed, and honest about why.
                Arc(frame, cx, cy, r, 20f, 30f, 300f, 1f, fg * 0.12f, fg * 0.4f, 48);
                GaugeLabel(frame, cx, cy, "UNKNOWN", "awaiting server", 1.1f,
                           fg * 0.5f, fg * 0.35f);
            }
            else
            {
                // 300 degrees, not 360: the gap IS the breach.
                Arc(frame, cx, cy, r, 20f, 30f, 300f, 1f, Danger * 0.15f, Danger, 48);
                GaugeLabel(frame, cx, cy, s.Breached ? "BREACH" : "OPEN",
                           s.Breached ? "SEAL LOST" : null, 1.1f, Danger, Danger * 0.8f);
            }

            float px = 268f;
            Text(frame, "PRESSURE", px, 122, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            // Say WHICH kind of not-sealed this is. "NO VOLUME" was reported on a
            // sealed ship and on a station, and told nobody anything: it could not
            // distinguish "the pressurisation system has no room here" from "there is
            // a room and it is open". The room's own numbers separate them.
            string pressure;
            if (s.Airtight) pressure = "INTEGRITY HELD";
            else if (s.Breached) pressure = "SEAL LOST";
            // UNKNOWN, not OPEN. On a dedicated-server client the pressurisation data
            // does not exist, and reporting that as "no sealed volume" is a false
            // negative dressed as a measurement - the exact failure this mod exists to
            // avoid. Say the instrument cannot answer.
            else if (s.SealStatus == Readings.SealNoGasSystem) pressure = "AWAITING SERVER";
            else if (s.SealStatus == Readings.SealAwaitingServer) pressure = "AWAITING SERVER";
            else if (s.SealStatus == Readings.SealProcessing) pressure = "PRESSURISING";
            else if (s.RoomBlocks <= 0) pressure = "NO ROOM HERE";
            else pressure = string.Format("OPEN ROOM  {0:P0}", s.RoomOxygen);

            Text(frame, pressure, px, 150, 0.8f, accent, TextAlignment.LEFT);

            // The room as the game sees it, which is the number to quote if this ever
            // disagrees with an air vent again.
            if (s.RoomBlocks > 0)
                Text(frame, string.Format("room {0} cells, O2 {1:P0}", s.RoomBlocks, s.RoomOxygen),
                     px, 178, 0.45f, fg * 0.4f, TextAlignment.LEFT);

            Text(frame, "DURATION", px, 214, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            Text(frame, s.Airtight ? Clock(s.SealedSeconds) : "--:--", px, 242, 0.9f, fg, TextAlignment.LEFT);

            // Radiation shelter is the reason this block exists, so it is shown here
            // rather than left for the reader to infer from the seal state.
            Text(frame, "PLANETARY RADIATION", px, 310, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            // "NOT BLOCKED" is a claim about shielding, and it was being made from a
            // seal reading the client did not have. Unknown seal, unknown shielding.
            bool sealKnown = s.SealStatus != Readings.SealNoGasSystem
                          && s.SealStatus != Readings.SealAwaitingServer;
            Text(frame, sealKnown ? (s.Airtight ? "BLOCKED" : "NOT BLOCKED") : "UNKNOWN",
                 px, 338, 0.85f, sealKnown ? accent : fg * 0.45f, TextAlignment.LEFT);

            if (!s.Airtight && s.Rad.Enabled && s.Rad.Accumulates)
            {
                Text(frame, "TIME TO CRITICAL", Pad, 428, 0.7f, Danger * 0.8f, TextAlignment.LEFT);
                Text(frame, Clock(s.Rad.SecondsToCritical), Pad, 456, 1.1f, Danger, TextAlignment.LEFT);
                float urgency = 1f - MathHelper.Clamp((float)(s.Rad.SecondsToCritical / 600.0), 0f, 1f);
                Bar(frame, Pad, 500, ContentWidth, 10f, urgency, Danger * 0.15f, Danger);
            }
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Weather", "Ground Truth: Weather")]
    public class TssWeather : TssBase
    {
        public TssWeather(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return TerminalApi.RoleWeather; } }
        protected override string Title { get { return "WEATHER"; } }

        // A Weather Station in space, or on an airless body, has no readings at all.
        private static readonly string[] IdleRemarks =
        {
            "no atmosphere; anemometer reading omitted as meaningless",
            "vacuum. conditions stable. conditions will remain stable.",
            "no weather system present, none expected",
            "instrument idle; this is the correct behaviour",
            "outlook unchanged, and has always been unchanged",
            "nothing to measure, measurement withheld",
        };

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.BodyHasWeather)
            {
                Text(frame, "NO WEATHER", Pad, 96, 1.8f, fg * 0.5f, TextAlignment.LEFT);
                Text(frame, "This body has no weather system.", Pad, 210, 0.75f, fg * 0.45f, TextAlignment.LEFT);
                Text(frame, Remark(IdleRemarks), Pad, 250, 0.45f, fg * 0.35f, TextAlignment.LEFT);
                return;
            }

            bool active = !string.IsNullOrEmpty(s.Weather);
            float cx = 128f, cy = 188f, r = 76f;

            // Intensity dial. Trend marks sit on the same arc: where it is now, and
            // which way it is going.
            // Clear reads calm green; a full strength event reads red.
            Color wxColor = !active ? Ok
                          : (s.WeatherIntensity > 0.75f ? Danger
                          : (s.WeatherIntensity > 0.35f ? Caution : Solar));

            Arc(frame, cx, cy, r, 20f, -140f, 280f, active ? s.WeatherIntensity : 0f,
                fg * 0.15f, wxColor, 44);
            // No sub-label under this dial. At cy 206 it lands at y 314, straight on top
            // of the SOLAR row - the trend moved to the right column, which has room.
            GaugeLabel(frame, cx, cy, active ? string.Format("{0:P0}", s.WeatherIntensity) : "CLEAR",
                       null, 1.2f, wxColor, fg * 0.55f);

            if (active)
                ArcMark(frame, cx, cy, r + 20f, 3f, 16f, -140f + 280f * s.WeatherIntensity, wxColor);

            float px = 268f;
            Text(frame, active ? s.Weather : "NO ACTIVE WEATHER", px, 112, 0.85f,
                 active ? wxColor : fg * 0.7f, TextAlignment.LEFT);

            if (active)
                Text(frame, s.Trend > 0 ? "BUILDING" : (s.Trend < 0 ? "EASING" : "STEADY"),
                     px, 140, 0.55f, fg * 0.55f, TextAlignment.LEFT);

            // Three distinct states, not two. Clock() renders a negative as "--:--",
            // which is right for "no weather" and wrong for "building, no estimate yet"
            // - the detail pane says "not yet peaked" and the LCD must not disagree.
            //
            // The estimate only exists after the peak, because it is derived from the
            // rise being symmetric with the decay. Before then there is nothing to
            // measure from, and saying so beats a row of dashes.
            double clearsIn = active ? s.SecondsToClear() : -1;
            string clearsText = !active ? "--:--"
                              : (clearsIn >= 0 ? Clock(clearsIn)
                              : (s.OnsetObserved ? "BUILDING" : "UNKNOWN"));

            Text(frame, "CLEARS IN", px, 170, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            Text(frame, clearsText, px, 196, clearsIn < 0 && active ? 0.75f : 0.95f,
                 active ? fg : fg * 0.6f, TextAlignment.LEFT);
            if (active && clearsIn < 0)
                Text(frame, s.OnsetObserved ? "no estimate until peak" : "onset not observed",
                     px, 222, 0.55f, fg * 0.45f, TextAlignment.LEFT);

            // The planet's declared range. Shown whenever there is no live estimate,
            // because that is exactly when a player still wants a number, and quietly
            // below it when there is - it is type-typical, not this storm.
            if (active && s.DeclaredMaxLength > 0)
                Text(frame, string.Format("typical {0:F0}-{1:F0} min",
                        s.DeclaredMinLength / 60.0, s.DeclaredMaxLength / 60.0),
                     px, clearsIn < 0 ? 244 : 222, 0.55f, fg * 0.45f, TextAlignment.LEFT);

            // Effects as bars against a fixed full-scale, so their lengths are
            // comparable between readings rather than only within one.
            // A declared hazard outranks the effect bars: it is the only thing on this
            // panel about whether standing here hurts. Injury leads, because 40 damage
            // kills faster than any dose rate in the game.
            if (active)
            {
                bool injury = s.Effect.HasHealth;
                bool rads = s.Effect.HasRadiation && s.Effect.RadiationGain > 0;
                bool shelter = s.Effect.HasRadiation && s.Effect.RadiationGain < 0;

                if (injury || rads)
                {
                    bool live = injury
                        ? s.WeatherIntensity >= s.Effect.HealthMinIntensity
                        : s.WeatherIntensity >= s.Effect.RadiationMinIntensity;

                    string label = injury
                        ? string.Format("INJURY {0:F0} DAMAGE", s.Effect.DamageMax)
                        : string.Format("RADIATION +{0:F2}/s", s.Effect.RadiationGain);

                    Text(frame, label, px, 274, 0.7f, live ? Danger : Caution, TextAlignment.LEFT);
                    Text(frame, live ? "ACTIVE at this intensity" : "starts at higher intensity",
                         px, 298, 0.5f, fg * 0.5f, TextAlignment.LEFT);
                }
                else if (shelter)
                {
                    // Rain reduces exposure. Worth saying plainly - a player sheltering
                    // from radiation would never guess that standing in a storm helps.
                    Text(frame, "RADIATION SHELTER", px, 274, 0.7f, Ok, TextAlignment.LEFT);
                    Text(frame, string.Format("{0:F2}/s while this holds", s.Effect.RadiationGain),
                         px, 298, 0.5f, fg * 0.5f, TextAlignment.LEFT);
                }
            }

            float by = 330f;
            // Labelled as the weather's effect, and dimmed at night when the panels are
            // making nothing regardless of what the weather is doing.
            bool sunUp = s.Env.SunElevation > 0 || s.Env.SunElevation < -900;
            Effect(frame, sunUp ? "SOLAR (WX)" : "SOLAR (WX) - NIGHT",
                   string.Format("{0:P0}", s.SolarMult), s.SolarMult / 1.5f, by, fg,
                   sunUp ? Solar : Solar * 0.45f);
            Effect(frame, "WIND (WX)",
                   s.MaxWindSpeed > 0 ? string.Format("{0:F0} m/s", s.WindSpeed)
                                      : string.Format("{0:P0}", s.WindMult),
                   s.WindMult / 2.5f, by + 60f, fg, Wind);
            // Only a sandstorm moves oxygen. When it does, red is the correct reading.
            Effect(frame, "OXYGEN (WX)", string.Format("{0:P0}", s.OxygenMult), s.OxygenMult, by + 120f, fg,
                   s.OxygenMult < 0.99f ? Danger : Oxy);
        }

        private void Effect(MySpriteDrawFrame frame, string label, string value, float frac,
                            float y, Color fg, Color accent)
        {
            Text(frame, label, Pad, y, 0.6f, fg * 0.5f, TextAlignment.LEFT);
            TextRight(frame, value, y, 0.7f, accent);
            Bar(frame, Pad, y + 32f, ContentWidth, 10f, MathHelper.Clamp(frac, 0f, 1f), fg * 0.12f, accent);
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Bio", "Ground Truth: Life Detection")]
    public class TssBio : TssBase
    {
        public TssBio(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        // Canvas units. Both scale with the surface, so these hold their proportions
        // from a small text panel up to a wide cockpit screen.
        private const float NearestIcon = 48f;
        private const float RowIcon = 26f;
        private const float RowStep = 32f;

        protected override float WantedRole { get { return TerminalApi.RoleBio; } }
        protected override string Title { get { return "LIFE DETECTION"; } }

        // An empty scan is still a reading, and Threshold Dynamics files it as such.
        private static readonly string[] NilRemarks =
        {
            "sample size zero, confidence total",
            "absence recorded as data",
            "survey complete, nothing to report, reported",
            "nothing alive in range, instrument functioning normally",
            "no returns; the instrument has no opinion as to why",
            "finding forwarded to Survey and Assessment",
        };

        private string NilRemark() { return Remark(NilRemarks); }

        protected override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg)
        {
            if (!s.Bio.Valid)
            {
                Text(frame, "SCANNING", Pad, 96, 1.5f, fg * 0.55f, TextAlignment.LEFT);
                return;
            }

            // The headline is deliberately compact. On a life detector the useful
            // content is the list of what is out there, not the total - so the total
            // gets one line and the list gets the rest of the panel.
            Text(frame, s.Bio.Count.ToString(), Pad, 62, 1.9f,
                 s.Bio.Count > 0 ? Ok : fg * 0.7f, TextAlignment.LEFT);
            TextRight(frame, string.Format("{0:F1} AVG   {1} PEAK   5 MIN", s.BioAvg, s.BioPeak),
                      78, 0.65f, fg * 0.55f);

            // Contacts are never folded into the organism count. Two different things
            // were detected and the panel says so on two lines.
            if (s.Bio.Contacts > 0)
                Text(frame, string.Format("{0} NON-BIOLOGICAL CONTACT{1}",
                        s.Bio.Contacts, s.Bio.Contacts == 1 ? "" : "S"),
                     Pad, 112, 0.62f, Danger, TextAlignment.LEFT);

            Rule(frame, 140, fg * 0.35f, 1.5f);

            if (s.Bio.Count == 0 && s.Bio.Contacts == 0)
            {
                Text(frame, "NIL RETURN", Pad, 172, 0.8f, fg * 0.5f, TextAlignment.LEFT);
                Text(frame, NilRemark(), Pad, 206, 0.5f, fg * 0.38f, TextAlignment.LEFT);
                Text(frame, string.Format("range {0:F0} m", s.Bio.Radius), Pad, 246, 0.7f, fg * 0.45f, TextAlignment.LEFT);
                return;
            }

            // Nearest contact gets the larger icon - it is the reading a player acts on.
            Icon(frame, IconFor(s.Bio.Nearest), Pad, 150, NearestIcon);
            float tx = Pad + NearestIcon + 12f;
            Text(frame, "NEAREST", tx, 152, 0.55f, fg * 0.55f, TextAlignment.LEFT);
            Text(frame, s.Bio.Nearest, tx, 174, 0.75f, fg, TextAlignment.LEFT);
            TextRight(frame, string.Format("{0:F0} m", s.Bio.NearestDist), 158, 0.8f, fg);
            // Relative leads on the panel - it is the one a pilot can act on without
            // any other mod installed. True north follows in smaller text when it is
            // actually available.
            TextRight(frame, string.Format("{0:F0} rel", s.Bio.NearestBearingRel), 182, 0.65f, fg * 0.75f);
            if (s.Bio.NearestBearingFrame == Readings.FramePlanetNorth)
                TextRight(frame, string.Format("{0:F0} true", s.Bio.NearestBearing), 208, 0.55f, fg * 0.5f);

            // The subtype text stays beside every icon: unmatched creatures fall back
            // to the paw marker, and the name is the only thing that then says what
            // they actually are.
            float y = 228;
            int shown = 0;
            foreach (var kv in s.Bio.Species)
            {
                if (y > 512 - RowStep) break;
                Icon(frame, IconFor(kv.Key), Pad, y - RowIcon * 0.22f, RowIcon);
                Text(frame, kv.Key, Pad + RowIcon + 10f, y, 0.68f, fg * 0.85f, TextAlignment.LEFT);
                TextRight(frame, kv.Value.ToString(), y, 0.68f, fg);
                y += RowStep;
                shown++;
            }

            if (shown < s.Bio.Species.Count)
                Text(frame, string.Format("+{0} more", s.Bio.Species.Count - shown), Pad, y, 0.6f, fg * 0.5f, TextAlignment.LEFT);
        }
    }
}
