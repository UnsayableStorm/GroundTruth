using System;
using System.Text;
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

        protected override void DrawStandalone(MySpriteDrawFrame frame, Color fg)
        {
            string text;
            Color color;
            if (!Alarm(fg, out text, out color))
                text = AllClear(fg, out color);

            // Fill the strip: as large as the shorter of the two constraints allows.
            // Height is capped at 62% so descenders and the surface bezel do not clip
            // the line.
            float byHeight = (Canvas * 0.62f) / LineHeight;
            float byWidth = (CanvasWidth - Pad * 2f) / Math.Max(1, text.Length * CharWidth);
            float scale = Math.Max(0.6f, Math.Min(byHeight, byWidth));

            // Centred both ways. A strip has no reading order to establish.
            Text(frame, text, CanvasWidth * 0.5f, (Canvas - LineHeight * scale) * 0.5f,
                 scale, color, TextAlignment.CENTER);
        }

        // ---- what is wrong, worst first ----

        private bool Alarm(Color fg, out string text, out Color color)
        {
            text = null;
            color = Danger;

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null && !hab.Airtight && hab.Breached)
            {
                text = "SEAL BREACHED";
                return true;
            }

            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null && rad.Rad.Enabled && rad.Rad.Accumulates)
            {
                double left = rad.Rad.SecondsToCritical;
                text = left >= 0 ? "RADIATION  " + Clock(left) : "RADIATION";
                color = left >= 0 && left < 300 ? Danger : Caution;
                return true;
            }

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null && !string.IsNullOrEmpty(wx.Weather)
                && (wx.Effect.HasHealth || (wx.Effect.HasRadiation && wx.Effect.RadiationGain > 0)))
            {
                text = wx.Weather.ToUpperInvariant();
                color = Danger;
                return true;
            }

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null && bio.Bio.Valid && bio.Bio.Contacts > 0)
            {
                text = bio.Bio.Contacts == 1 ? "CONTACT" : bio.Bio.Contacts + " CONTACTS";
                color = Caution;
                return true;
            }

            return false;
        }

        // ---- nothing wrong: prove the instruments are awake ----

        private string AllClear(Color fg, out Color color)
        {
            color = Ok;
            var sb = new StringBuilder();
            int present = 0;

            var wx = StateForRole(Instruments.RoleWeather);
            if (wx != null)
            {
                present++;
                sb.Append(!wx.BodyHasWeather ? "NO WX"
                          : (string.IsNullOrEmpty(wx.Weather) ? "CLEAR" : wx.Weather.ToUpperInvariant()));
            }

            var rad = StateForRole(Instruments.RoleRadiation);
            if (rad != null)
            {
                present++;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(!rad.Rad.Enabled || rad.Rad.IntensitySetting <= 0 ? "RAD OFF" : "RAD SAFE");
            }

            var hab = StateForRole(Instruments.RoleHabitat);
            if (hab != null)
            {
                present++;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(hab.Airtight ? "SEALED" : "OPEN");
            }

            var bio = StateForRole(Instruments.RoleBio);
            if (bio != null)
            {
                present++;
                if (sb.Length > 0) sb.Append("  ");
                sb.Append(!bio.Bio.Valid ? "LIFE --" : "LIFE " + bio.Bio.Count);
            }

            // No instruments at all is a real state and says so, rather than showing an
            // all-clear nobody measured.
            if (present == 0)
            {
                color = fg * 0.5f;
                return "NO INSTRUMENTS";
            }

            return sb.ToString();
        }
    }
}
