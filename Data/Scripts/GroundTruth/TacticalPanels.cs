using System;
using System.Collections.Generic;
using Sandbox.Game.GameSystems.TextSurfaceScripts;
using Sandbox.ModAPI;
using VRage.Game.GUI.TextPanel;
using VRage.Game.ModAPI;
using VRageMath;

namespace GroundTruth
{
    // The tactical LCD apps: Contact List, Tactical Radar, Target Track
    // (TACTICAL_PLAN Phase 2).
    //
    // They read the dish's contact table and never scan - the same rule as every other
    // app here. A List and a Radar on one ship therefore cannot disagree about what is
    // out there: they are two drawings of one table. Reading the table is also what
    // keeps it scanning (Tactical.TableFor marks it watched), so a ship with a radar
    // screen has a live picture without anyone opening a terminal.
    //
    // The dish is found the way every app finds its instrument - nearest of the role on
    // the panel's own grid, overridable by name with "Radar =" in Custom Data or with the
    // panel's terminal dropdown.
    //
    // Display settings, all optional, in the panel's [GroundTruth] or [GroundTruth.N]:
    //
    //   Range = 5          plot range in km (Radar). Default: just beyond the farthest
    //                      contact, in steps, never past the scan range.
    //   Sort = range       range | bearing | closing | class   (List)
    //   Rows = 8           row cap (List). Default: as many as fit.
    //   Vectors = on       velocity vectors, 30 s of travel (Radar). Default off.
    //   Labels = tracked   tracked | all | none (Radar). Default tracked.
    //   Orientation = ship ship | north (Radar). North needs a gravity well.
    //
    // Anything unparseable is that setting's default. Garbage in Custom Data must never
    // cost a player their screen.
    public abstract class TacticalTssBase : TssBase
    {
        protected TacticalTssBase(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override float WantedRole { get { return Instruments.RoleRadar; } }

        protected Tactical.Table Table;
        protected Tactical.Contact Tracked;
        protected bool TrackedManual;
        protected readonly List<Tactical.Contact> Contacts = new List<Tactical.Contact>();

        protected IMyTerminalBlock Dish { get { return Instrument; } }

        protected sealed override void Draw(MySpriteDrawFrame frame, GroundTruthSession.BlockState s, Color fg)
        {
            var dish = Dish;
            if (dish == null) return;

            Table = Tactical.TableFor(dish, GroundTruthSession.Seconds);
            Tactical.Sorted(Table, dish, Contacts);
            Tracked = TacticalSelection.Tracked(Table, dish, out TrackedManual);

            if (Table != null && Table.Offline)
            {
                // Said in the header area of every tactical app, because an offline
                // sensor's list is last-known data and must not pass for a live picture.
                TextRight(frame, "SENSOR OFFLINE", 40, 0.55f, Danger);
            }

            DrawTactical(frame, dish, fg);
        }

        protected abstract void DrawTactical(MySpriteDrawFrame frame, IMyTerminalBlock dish, Color fg);

        // ---- shared vocabulary ----

        protected Color RelationColor(Tactical.Contact c, Color fg)
        {
            if (c.Relation == Tactical.RelationUnknown) return Caution;
            switch ((VRage.Game.MyRelationsBetweenPlayerAndBlock)c.Relation)
            {
                case VRage.Game.MyRelationsBetweenPlayerAndBlock.Enemies: return Danger;
                case VRage.Game.MyRelationsBetweenPlayerAndBlock.Owner:
                case VRage.Game.MyRelationsBetweenPlayerAndBlock.FactionShare: return Ok;
                case VRage.Game.MyRelationsBetweenPlayerAndBlock.Friends: return Oxy;
                case VRage.Game.MyRelationsBetweenPlayerAndBlock.NoOwnership: return fg * 0.6f;
                default: return fg * 0.9f;
            }
        }

        protected static string NameOf(Tactical.Contact c)
        {
            return string.IsNullOrEmpty(c.Name) ? "UNIDENTIFIED" : c.Name;
        }

        protected static string ShortClass(Tactical.Contact c)
        {
            if (c.IsStatic) return "STN";
            return c.IsLarge ? "LG" : "SM";
        }

        protected static string Range(double m)
        {
            if (m < 0) return "--";
            return m >= 1000 ? string.Format("{0:F1}km", m / 1000.0) : string.Format("{0:F0}m", m);
        }

        // Unavailable is "--", never 0: a bearing of 0 is a real bearing.
        protected static string Deg(double d)
        {
            return d < 0 ? "--" : string.Format("{0:000}", d);
        }

        protected string TrackLine()
        {
            if (Tracked != null)
                return (TrackedManual ? "TRACK " : "AUTO ") + NameOf(Tracked);
            return TacticalSelection.StoredId(Dish) != 0 ? "TRACK expired - auto: no enemy" : "AUTO no enemy";
        }

        protected string SettingLower(string key)
        {
            var v = Selection.Setting(key);
            return v == null ? null : v.Trim().ToLowerInvariant();
        }

        protected double SettingKm(string key, double fallbackKm)
        {
            var v = SettingLower(key);
            if (v == null) return fallbackKm;
            double scale = 1;
            if (v.EndsWith("km")) v = v.Substring(0, v.Length - 2);
            else if (v.EndsWith("m")) { v = v.Substring(0, v.Length - 1); scale = 0.001; }
            double d;
            if (!double.TryParse(v.Trim(), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out d)) return fallbackKm;
            d *= scale;
            return d > 0.05 && d < 1000 ? d : fallbackKm;
        }

        protected int SettingInt(string key, int fallback)
        {
            var v = SettingLower(key);
            int i;
            return v != null && int.TryParse(v, out i) && i > 0 && i < 200 ? i : fallback;
        }

        protected bool SettingOn(string key, bool fallback)
        {
            var v = SettingLower(key);
            if (v == null) return fallback;
            if (v == "on" || v == "true" || v == "yes" || v == "1") return true;
            if (v == "off" || v == "false" || v == "no" || v == "0") return false;
            return fallback;
        }

        // A straight line in canvas units, as one rotated quad. Rotation is clockwise
        // from screen-up, the same convention the Arc helper uses.
        protected void Line(MySpriteDrawFrame frame, float x1, float y1, float x2, float y2, float thick, Color color)
        {
            float dx = x2 - x1, dy = y2 - y1;
            float len = (float)Math.Sqrt(dx * dx + dy * dy);
            if (len < 0.5f) return;
            var s = MySprite.CreateSprite("SquareSimple",
                new Vector2(Org.X + (x1 + dx * 0.5f) * U, Org.Y + (y1 + dy * 0.5f) * U),
                new Vector2(thick * U, len * U));
            s.Color = color;
            s.RotationOrScale = (float)Math.Atan2(dx, -dy);
            frame.Add(s);
        }

        // A thin full circle. Not the Arc helper: Arc's quads are sized for a fat gauge
        // band, and at a hairline thickness they come out as spokes. Here each quad is
        // long along the circle and thin across it, which is what a ring is. Rotation
        // direction does not matter for a ring, so this is safe whichever way SE turns.
        protected void Ring(MySpriteDrawFrame frame, float cx, float cy, float radius, float thick, Color color, int segments)
        {
            float segLen = (float)(2.0 * Math.PI * radius / segments) * 1.15f;
            for (int i = 0; i < segments; i++)
            {
                double a = 2.0 * Math.PI * (i + 0.5) / segments;
                var s = MySprite.CreateSprite("SquareSimple",
                    new Vector2(Org.X + (cx + radius * (float)Math.Sin(a)) * U, Org.Y + (cy - radius * (float)Math.Cos(a)) * U),
                    new Vector2(segLen * U, thick * U));
                s.Color = color;
                s.RotationOrScale = (float)a;
                frame.Add(s);
            }
        }

        protected void Sprite(MySpriteDrawFrame frame, string sprite, float cx, float cy, float size, Color color, float rotation)
        {
            var s = MySprite.CreateSprite(sprite, new Vector2(Org.X + cx * U, Org.Y + cy * U), new Vector2(size * U, size * U));
            s.Color = color;
            s.RotationOrScale = rotation;
            frame.Add(s);
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Contacts", "Ground Truth: Contact List")]
    public class TssContacts : TacticalTssBase
    {
        public TssContacts(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title { get { return "CONTACTS"; } }

        protected override void AddPresetSettings(List<string[]> keys)
        {
            keys.Add(new[] { "Sort", "Sort: range, bearing, closing or class. Empty = range." });
            keys.Add(new[] { "Rows", "Rows: most contacts listed, e.g. 8. Empty = as many as fit." });
        }

        private const float RowStep = 30f;
        private const float RowScale = 0.58f;
        private const float Top = 108f;

        private readonly List<Tactical.Contact> _rows = new List<Tactical.Contact>();

        protected override void DrawTactical(MySpriteDrawFrame frame, IMyTerminalBlock dish, Color fg)
        {
            int lost = 0;
            for (int i = 0; i < Contacts.Count; i++) if (Contacts[i].State == Tactical.StateLost) lost++;

            Text(frame, string.Format("{0}", Contacts.Count - lost), Pad, 64, 1.1f, Contacts.Count - lost > 0 ? fg : fg * 0.6f, TextAlignment.LEFT);
            Text(frame, lost > 0 ? string.Format("DETECTED   +{0} LOST", lost) : "DETECTED", Pad + 52f, 72, 0.55f, fg * 0.55f, TextAlignment.LEFT);
            TextRight(frame, TrackLine(), 72, 0.55f, Tracked != null ? Caution : fg * 0.5f);

            var pos = dish.GetPosition();
            string sort = SettingLower("sort") ?? "range";
            _rows.Clear();
            _rows.AddRange(Contacts);
            Sort(_rows, sort, dish, pos);

            float nameX = Pad + 70f;
            float w = CanvasWidth - Pad;

            // Column heads, dim, so a number is never read without knowing which it is.
            Color head = fg * 0.4f;
            Text(frame, "CLS", Pad + 14f, Top - 4, 0.45f, head, TextAlignment.LEFT);
            Text(frame, "NAME", nameX, Top - 4, 0.45f, head, TextAlignment.LEFT);
            Text(frame, "RANGE", w - 186f, Top - 4, 0.45f, head, TextAlignment.RIGHT);
            Text(frame, "BRG", w - 110f, Top - 4, 0.45f, head, TextAlignment.RIGHT);
            Text(frame, "CLOSING", w, Top - 4, 0.45f, head, TextAlignment.RIGHT);
            Rule(frame, Top + 16f, fg * 0.25f, 1f);

            int fit = (int)((512f - Pad - (Top + 24f)) / RowStep);
            int cap = Math.Min(fit, SettingInt("rows", fit));
            int nameChars = Math.Max(4, (int)((w - 200f - nameX) / (CharWidthAt1 * RowScale)));

            float y = Top + 24f;
            for (int i = 0; i < _rows.Count && i < cap; i++)
            {
                var c = _rows[i];
                bool isTracked = Tracked != null && c.Id == Tracked.Id;
                bool isLost = c.State == Tactical.StateLost;
                Color col = RelationColor(c, fg);
                if (isLost) col = col * 0.45f;

                if (isTracked)
                {
                    // A band behind the row, not a colour change: the relation colour is
                    // information and must survive being tracked.
                    var band = MySprite.CreateSprite("SquareSimple",
                        new Vector2(Org.X + (CanvasWidth * 0.5f) * U, Org.Y + (y + 11f) * U),
                        new Vector2((CanvasWidth - Pad) * U, (RowStep - 4f) * U));
                    band.Color = Caution * 0.18f;
                    frame.Add(band);
                    Text(frame, ">", Pad - 6f, y, RowScale, Caution, TextAlignment.LEFT);
                }

                string name = NameOf(c);
                if (name.Length > nameChars) name = name.Substring(0, Math.Max(1, nameChars - 2)) + "..";

                double range = TacticalMath.Range(pos, c.Position);
                double rel = Tactical.RelativeBearing(dish, c.Position);

                Text(frame, ShortClass(c), Pad + 14f, y, RowScale, col, TextAlignment.LEFT);
                Text(frame, name, nameX, y, RowScale, col, TextAlignment.LEFT);
                Text(frame, Range(range), w - 186f, y, RowScale, col, TextAlignment.RIGHT);
                Text(frame, Deg(rel), w - 110f, y, RowScale, col, TextAlignment.RIGHT);

                // A lost contact's motion is last-known, so no closing rate is claimed.
                string closing = isLost
                    ? string.Format("LOST {0:F0}s", GroundTruthSession.Seconds - c.LastSeen)
                    : string.Format("{0:+0.0;-0.0;0.0}", Tactical.ClosingRate(dish, c));
                Text(frame, closing, w, y, RowScale, col, TextAlignment.RIGHT);
                y += RowStep;
            }

            if (_rows.Count > cap)
                Text(frame, string.Format("+{0} more", _rows.Count - cap), Pad, y, 0.5f, fg * 0.45f, TextAlignment.LEFT);
            else if (_rows.Count == 0)
            {
                Text(frame, "NO CONTACTS", Pad, Top + 30f, 0.9f, fg * 0.5f, TextAlignment.LEFT);
                Text(frame, "silent ships beyond the dark-return range do not appear", Pad, Top + 72f, 0.45f, fg * 0.38f, TextAlignment.LEFT);
            }

            TextRight(frame, "SORT " + sort.ToUpperInvariant() + "   bearings rel. to ship's nose   closing m/s", 512f - Pad + 4f, 0.4f, fg * 0.35f);
            _rows.Clear();
        }

        private static void Sort(List<Tactical.Contact> rows, string sort, IMyTerminalBlock dish, Vector3D pos)
        {
            // Contacts arrives nearest-first already; every other order is a stable
            // re-sort on top of that, so ties stay nearest-first.
            if (sort == "bearing")
                StableSort(rows, c => { double b = Tactical.RelativeBearing(dish, c.Position); return b < 0 ? 999 : b; });
            else if (sort == "closing")
                StableSort(rows, c => c.State == Tactical.StateLost ? double.MaxValue : -Tactical.ClosingRate(dish, c));
            else if (sort == "class")
                StableSort(rows, c => c.IsStatic ? 0 : (c.IsLarge ? 1 : 2));
        }

        // Insertion sort: stable, allocation-free, and the list is at most 64 long.
        private static void StableSort(List<Tactical.Contact> rows, Func<Tactical.Contact, double> key)
        {
            for (int i = 1; i < rows.Count; i++)
            {
                var item = rows[i];
                double k = key(item);
                int j = i - 1;
                while (j >= 0 && key(rows[j]) > k) { rows[j + 1] = rows[j]; j--; }
                rows[j + 1] = item;
            }
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Radar", "Ground Truth: Tactical Radar")]
    public class TssRadar : TacticalTssBase
    {
        public TssRadar(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title { get { return "TACTICAL"; } }

        protected override void AddPresetSettings(List<string[]> keys)
        {
            keys.Add(new[] { "Range", "Range: outer ring, e.g. 5km or 2000m. Empty = the sensor's full range (view distance)." });
            keys.Add(new[] { "Orientation", "Orientation: ship (nose up) or north (north up, on a planet). Empty = ship." });
            keys.Add(new[] { "Vectors", "Vectors: on or off - a velocity line on each contact. Empty = off." });
            keys.Add(new[] { "Labels", "Labels: tracked, all or none - which contacts get a name. Empty = tracked." });
        }


        protected override void DrawTactical(MySpriteDrawFrame frame, IMyTerminalBlock dish, Color fg)
        {
            var pos = dish.GetPosition();

            // ---- frame of reference (D5) ----
            // Ship-relative by default: the ship's nose is up, the plane is the ship's own
            // horizontal. North-up only where a north exists (ENGINE_TRAPS 7).
            var gm = dish.CubeGrid.WorldMatrix;
            Vector3D fwd = gm.Forward, right = gm.Right, up = gm.Up;
            bool northUp = false;
            if ((SettingLower("orientation") ?? "ship") == "north")
            {
                float interference;
                var g = MyAPIGateway.Physics.CalculateNaturalGravityAt(pos, out interference);
                if (g.LengthSquared() > 0.000001)
                {
                    var u = -Vector3D.Normalize(g);
                    var axis = new Vector3D(0, 1, 0);
                    var north = axis - u * Vector3D.Dot(axis, u);
                    if (north.LengthSquared() > 0.0075)
                    {
                        up = u;
                        fwd = Vector3D.Normalize(north);
                        right = Vector3D.Cross(fwd, up);
                        northUp = true;
                    }
                }
            }

            // ---- range ----
            // The outer ring IS the sensor's range - the world's view distance (Tactical,
            // ScanRadius) - so the plot shows everything the dish can see and the edge of
            // the screen means the edge of detection. Jason, 2026-10-07. This was an
            // auto-zoom to the next step above the farthest contact, which put the outer
            // ring at 7.5 km with a 15 km sensor and hid how far the dish actually reaches.
            // A "Range" key in Custom Data still overrides it for a closer view.
            double scanKm = Table != null && Table.ScanRadius > 0 ? Table.ScanRadius / 1000.0 : 10;
            double rangeKm = SettingKm("range", scanKm);
            double rangeM = rangeKm * 1000.0;

            // ---- plot geometry ----
            float top = 76f, bottom = 512f - Pad - 26f;
            float r = Math.Min((bottom - top) * 0.5f, CanvasWidth * 0.5f - Pad);
            float cx = CanvasWidth * 0.5f, cy = top + (bottom - top) * 0.5f;

            Color ring = fg * 0.22f;
            for (int k = 1; k <= 4; k++)
            {
                float rr = r * k / 4f;
                Ring(frame, cx, cy, rr, k == 4 ? 2f : 1.2f, ring, 32 + k * 12);
                Text(frame, Range(rangeM * k / 4.0), cx + rr * 0.72f + 4f, cy - rr * 0.72f - 14f, 0.4f, fg * 0.4f, TextAlignment.LEFT);
            }

            // The dark-return boundary, where it fits on the plot: inside it nothing hides.
            if (Tactical.DarkReturnRange < rangeM)
                Ring(frame, cx, cy, (float)(r * Tactical.DarkReturnRange / rangeM), 1.2f, Caution * 0.25f, 48);

            // Own ship. Ship-relative: always nose-up. North-up: rotated to the heading.
            float heading = 0f;
            if (northUp)
            {
                var f = gm.Forward - up * Vector3D.Dot(gm.Forward, up);
                if (f.LengthSquared() > 1e-6)
                    heading = (float)Math.Atan2(Vector3D.Dot(f, right), Vector3D.Dot(f, fwd));
            }
            Sprite(frame, "Triangle", cx, cy, 16f, fg * 0.9f, heading);

            bool vectors = SettingOn("vectors", false);
            string labels = SettingLower("labels") ?? "tracked";

            for (int i = Contacts.Count - 1; i >= 0; i--)   // nearest drawn last, on top
            {
                var c = Contacts[i];
                var rel = c.Position - pos;
                float px = (float)(Vector3D.Dot(rel, right) / rangeM * r);
                float py = (float)(-Vector3D.Dot(rel, fwd) / rangeM * r);
                float plane = (float)Math.Sqrt(px * px + py * py);
                bool clipped = plane > r;
                if (clipped) { px *= r / plane; py *= r / plane; }

                float x = cx + px, y = cy + py;
                bool lost = c.State == Tactical.StateLost;
                Color col = RelationColor(c, fg);
                if (lost) col = col * 0.6f;

                // Elevation stalk: the marker rides above or below its plotted point by
                // its height off the plane, on the plot's own scale, capped so a contact
                // far overhead does not leave the screen.
                float h = clipped ? 0f : (float)(Vector3D.Dot(rel, up) / rangeM * r);
                h = MathHelper.Clamp(h, -r * 0.5f, r * 0.5f);
                float my = y - h;
                if (Math.Abs(h) > 2f)
                {
                    Line(frame, x, y, x, my, 1.5f, col * 0.6f);
                    Sprite(frame, "SquareSimple", x, y, 4f, col * 0.6f, 0f);
                }

                if (vectors && !lost)
                {
                    var v = c.Velocity * 30.0;
                    float vx = (float)(Vector3D.Dot(v, right) / rangeM * r);
                    float vy = (float)(-Vector3D.Dot(v, fwd) / rangeM * r);
                    Line(frame, x, my, x + vx, my + vy, 1.5f, col * 0.75f);
                }

                // Marker by class. Lost contacts are hollow: drawn where they were, not
                // where they are.
                string sprite = lost ? "CircleHollow" : (c.IsStatic ? "SquareSimple" : (c.IsLarge ? "Triangle" : "Circle"));
                float size = c.IsStatic || c.IsLarge ? 16f : 12f;
                Sprite(frame, sprite, x, my, clipped ? size * 0.7f : size, col, 0f);

                bool isTracked = Tracked != null && c.Id == Tracked.Id;
                if (isTracked) Sprite(frame, "CircleHollow", x, my, 30f, Caution, 0f);

                if (labels == "all" || (labels != "none" && isTracked))
                {
                    string n = NameOf(c);
                    if (n.Length > 18) n = n.Substring(0, 16) + "..";
                    Text(frame, n, x + 12f, my - 8f, 0.42f, isTracked ? Caution : col, TextAlignment.LEFT);
                }
            }

            Text(frame, string.Format("{0}  {1}", Range(rangeM), northUp ? "NORTH-UP" : "SHIP-RELATIVE"),
                 Pad, 512f - Pad - 14f, 0.45f, fg * 0.45f, TextAlignment.LEFT);
            TextRight(frame, TrackLine(), 512f - Pad - 14f, 0.45f, Tracked != null ? Caution : fg * 0.45f);
        }
    }

    // ------------------------------------------------------------------

    [MyTextSurfaceScript("GT_Track", "Ground Truth: Target Track")]
    public class TssTrack : TacticalTssBase
    {
        public TssTrack(IMyTextSurface surface, IMyCubeBlock block, Vector2 size)
            : base(surface, block, size) { }

        protected override string Title { get { return "TARGET TRACK"; } }

        protected override void DrawTactical(MySpriteDrawFrame frame, IMyTerminalBlock dish, Color fg)
        {
            var c = Tracked;
            if (c == null)
            {
                Text(frame, "NO TRACK", Pad, 96, 1.5f, fg * 0.55f, TextAlignment.LEFT);
                bool expired = TacticalSelection.StoredId(dish) != 0;
                Text(frame, expired ? "the selected contact has expired" : "automatic tracking: no enemy detected",
                     Pad, 150, 0.6f, fg * 0.45f, TextAlignment.LEFT);
                Text(frame, "select a contact on the dish's terminal or with its toolbar actions",
                     Pad, 180, 0.45f, fg * 0.35f, TextAlignment.LEFT);
                return;
            }

            bool lost = c.State == Tactical.StateLost;
            Color rc = RelationColor(c, fg);
            var pos = dish.GetPosition();

            string name = NameOf(c);
            int max = (int)(ContentWidth / (CharWidthAt1 * 1.0f));
            if (name.Length > max) name = name.Substring(0, Math.Max(1, max - 2)) + "..";
            Text(frame, name, Pad, 66, 1.0f, lost ? rc * 0.6f : rc, TextAlignment.LEFT);

            string mode = TrackedManual ? "SELECTED" : "AUTO: NEAREST ENEMY";
            string state = lost ? string.Format("LOST {0:F0}s - LAST KNOWN", GroundTruthSession.Seconds - c.LastSeen) : "TRACKED";
            Text(frame, Tactical.RelationName(c.Relation) + "   " + Tactical.ClassOf(c).ToUpperInvariant() + "   " + Detected(c),
                 Pad, 104, 0.55f, fg * 0.7f, TextAlignment.LEFT);
            Text(frame, state + "   " + mode, Pad, 128, 0.55f, lost ? Caution : fg * 0.7f, TextAlignment.LEFT);
            Rule(frame, 156, fg * 0.3f, 1.5f);

            int frameKind;
            double range = TacticalMath.Range(pos, c.Position);
            double brg = Tactical.Bearing(dish, c.Position, out frameKind);
            double rel = Tactical.RelativeBearing(dish, c.Position);
            double elev = Tactical.Elevation(dish, c.Position);

            float y = 170f;
            Row(frame, "RANGE", Range(range), ref y, fg);
            Row(frame, "BEARING", frameKind == Readings.FramePlanetNorth
                ? Deg(brg) + " true   " + Deg(rel) + " rel"
                : Deg(rel) + " rel", ref y, fg);
            Row(frame, "ELEVATION", string.Format("{0:+0;-0;0} deg", elev), ref y, fg);

            if (lost)
            {
                // Motion of a lost contact is what it was doing when last seen, and is
                // labelled as such rather than shown as if it were current.
                Row(frame, "SPEED", string.Format("{0:F1} m/s  (last known)", c.Velocity.Length()), ref y, fg * 0.6f);
                Row(frame, "CLOSING", "--  (not tracking)", ref y, fg * 0.6f);
            }
            else
            {
                double closing = Tactical.ClosingRate(dish, c);
                Row(frame, "SPEED", string.Format("{0:F1} m/s", c.Velocity.Length()), ref y, fg);
                Row(frame, "CLOSING", string.Format("{0:+0.0;-0.0;0.0} m/s  {1}", closing, closing > 0.5 ? "closing" : (closing < -0.5 ? "opening" : "holding")), ref y,
                    closing > 0.5 ? Caution : fg);
            }

            var d = c.Dimensions;
            Row(frame, "SIZE", string.Format("{0:F0} x {1:F0} x {2:F0} m", d.X, d.Y, d.Z), ref y, fg);
            Row(frame, "HELD FOR", Clock(GroundTruthSession.Seconds - c.FirstSeen), ref y, fg * 0.8f);

            if (c.DetectedBy == Tactical.DetectedByDarkReturn)
            {
                Text(frame, "DARK RETURN - identity and allegiance unknown", Pad, y + 6f, 0.48f, Caution * 0.8f, TextAlignment.LEFT);
                Text(frame, "nothing aboard is broadcasting", Pad, y + 30f, 0.45f, fg * 0.4f, TextAlignment.LEFT);
                return;
            }

            if (c.Broadcasters.Count > 0)
            {
                Text(frame, "SIGNALS", Pad, y + 6f, 0.45f, fg * 0.45f, TextAlignment.LEFT);
                y += 30f;
                for (int i = 0; i < c.Broadcasters.Count && y < 512f - Pad - 10f; i++)
                {
                    var b = c.Broadcasters[i];
                    Text(frame, (b.IsBeacon ? "beacon  " : "antenna ") + b.Label, Pad, y, 0.48f, fg * 0.75f, TextAlignment.LEFT);
                    TextRight(frame, Range(b.Radius), y, 0.48f, fg * 0.55f);
                    y += 24f;
                }
            }
        }

        private static string Detected(Tactical.Contact c)
        {
            if (c.DetectedBy == Tactical.DetectedByOurs) return "OURS";
            if (c.DetectedBy == Tactical.DetectedByBroadcast) return "BROADCAST";
            return "DARK RETURN";
        }

        private void Row(MySpriteDrawFrame frame, string label, string value, ref float y, Color fg)
        {
            Text(frame, label, Pad, y, 0.55f, fg * 0.5f, TextAlignment.LEFT);
            Text(frame, value, Pad + 150f, y, 0.6f, fg, TextAlignment.LEFT);
            y += 30f;
        }
    }
}
