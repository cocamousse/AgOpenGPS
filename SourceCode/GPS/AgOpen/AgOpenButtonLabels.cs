using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Text;
using System.Linq;
using System.Windows.Forms;
using AgLibrary.Logging;

namespace AgOpenGPS.AgOpen
{
    /// <summary>A button to label: its name (the key in the labels file) and where it is on screen.</summary>
    public sealed class AgOpenLabelTarget
    {
        public string Key;
        public Rectangle Screen;
    }

    /// <summary>Buttons whose labels go on the same side, lined up together.</summary>
    public sealed class AgOpenLabelGroup
    {
        public readonly LabelSide Side;

        /// <summary>Buttons side by side in a row share one label ("Tilt down · Tilt up").</summary>
        public readonly bool JoinRows;

        /// <summary>Other labels may cover these buttons when there's no other room (the simulator's).</summary>
        public readonly bool Soft;

        public readonly List<AgOpenLabelTarget> Targets = new List<AgOpenLabelTarget>();

        public AgOpenLabelGroup(LabelSide side, bool joinRows = false, bool soft = false)
        {
            Side = side;
            JoinRows = joinRows;
            Soft = soft;
        }

        /// <summary>Add one control, if it's on screen.</summary>
        public void Add(Control control)
        {
            if (control == null || control.IsDisposed || !control.Visible || string.IsNullOrEmpty(control.Name)) return;
            if (control.Width < 8 || control.Height < 8) return;
            Targets.Add(new AgOpenLabelTarget { Key = control.Name, Screen = control.RectangleToScreen(control.ClientRectangle) });
        }

        /// <summary>Add the buttons (and drop-down lists) directly inside a panel.</summary>
        public void AddButtonsIn(Control container)
        {
            if (container == null || !container.Visible) return;
            foreach (Control child in container.Controls)
            {
                if (child is ButtonBase || child is ComboBox) Add(child);
            }
        }

        /// <summary>Add the items of a menu or status strip (e.g. AgOpenGPS's drop-down buttons).</summary>
        public void AddItemsIn(ToolStrip strip)
        {
            if (strip == null || !strip.Visible) return;
            foreach (ToolStripItem item in strip.Items)
            {
                if (!item.Available || string.IsNullOrEmpty(item.Name) || item.Width < 8 || item.Height < 8) continue;
                Targets.Add(new AgOpenLabelTarget { Key = item.Name, Screen = strip.RectangleToScreen(item.Bounds) });
            }
        }
    }

    /// <summary>
    /// Labels for AgOpenGPS's buttons, shown only when wanted:
    /// - A tap on the map shows a label beside every main-screen button for a few
    ///   seconds (beside the side columns, above the bottom row), then they fade.
    ///   A tap on one of AgOpenGPS's own map buttons (zoom, U-turn, ...) just does
    ///   that; every tap still does whatever it does in AgOpenGPS.
    /// - While a pop-out menu (Tracks, View) is open, its buttons are labelled; a
    ///   tap on the map closes the menu. Menus stay open a little longer than in
    ///   stock AgOpenGPS, to give time to read.
    /// - Any other screen AgOpenGPS opens gets labels beside its picture-only
    ///   buttons while it's open (a tap on the main screen clears them for screens
    ///   that don't block it).
    /// The words come from AgOpenButtonNames (a text file that can be edited).
    /// </summary>
    public sealed class AgOpenButtonLabels : IMessageFilter
    {
        private const int MainLabelsMs = 6000;
        private const int FadeMs = 450;
        private const int CheckEveryMs = 100;
        private const int ColumnGap = 10; // between a side column and its labels
        private const int RowGap = 4;     // between a row of buttons and its labels (room is tight above the bottom row)

        private const int WM_LBUTTONDOWN = 0x0201;
        private const int WM_RBUTTONDOWN = 0x0204;

        public static readonly Font SideFont = new Font("Tahoma", 14F, FontStyle.Bold);
        public static readonly Font RowFont = new Font("Tahoma", 12F, FontStyle.Bold);
        public static readonly Font ScreenFont = new Font("Tahoma", 11F, FontStyle.Bold);

        // Screens that never get labels.
        private static readonly HashSet<string> SkipScreens = new HashSet<string>(StringComparer.Ordinal)
        {
            "FormTimedMessage", "FormDialog"
        };

        private readonly FormGPS mf;
        private readonly Timer timer = new Timer();
        private readonly Dictionary<Form, AgOpenLabelOverlay> screenOverlays = new Dictionary<Form, AgOpenLabelOverlay>();
        private readonly Dictionary<Form, string> screenShown = new Dictionary<Form, string>();
        private readonly HashSet<Form> cleared = new HashSet<Form>();
        private readonly Dictionary<string, Size> measured = new Dictionary<string, Size>();
        private readonly Bitmap measureBitmap = new Bitmap(1, 1);
        private readonly Graphics measure;

        private AgOpenLabelOverlay mainOverlay;
        private string mainShown = "";
        private DateTime mainUntil = DateTime.MinValue;
        private DateTime fadeStart = DateTime.MinValue;
        private bool menuWasOpen;
        private bool filtering;
        private bool problemLogged;

        public AgOpenButtonLabels(FormGPS mf)
        {
            this.mf = mf;
            measure = Graphics.FromImage(measureBitmap);
            measure.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            timer.Interval = CheckEveryMs;
            timer.Tick += (s, e) => Check();
        }

        public void Start()
        {
            if (!filtering)
            {
                Application.AddMessageFilter(this);
                filtering = true;
            }
            timer.Start();
        }

        public void Stop()
        {
            timer.Stop();
            if (filtering)
            {
                Application.RemoveMessageFilter(this);
                filtering = false;
            }
            if (mainOverlay != null && !mainOverlay.IsDisposed) mainOverlay.Close();
            foreach (AgOpenLabelOverlay overlay in screenOverlays.Values)
            {
                if (!overlay.IsDisposed) overlay.Close();
            }
            screenOverlays.Clear();
        }

        /// <summary>
        /// Sees every touch before AgOpenGPS does, and never uses one up: AgOpenGPS
        /// still gets it as usual.
        /// </summary>
        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_LBUTTONDOWN && m.Msg != WM_RBUTTONDOWN) return false;
            try
            {
                Control target = Control.FromChildHandle(m.HWnd);
                if (target == null || target.FindForm() != mf) return false;
                TouchedMainScreen(target);
            }
            catch (Exception ex)
            {
                NoteProblem(ex);
            }
            return false;
        }

        private void TouchedMainScreen(Control target)
        {
            // Screens that don't block the main one: a touch there clears their labels.
            foreach (Form screen in screenOverlays.Keys)
            {
                if (!screen.Modal) cleared.Add(screen);
            }

            // That touch closes an AgOpen box (and is used up by it): no labels.
            if (AgOpenPopupOpen()) return;

            if (target == mf.oglMain)
            {
                if (mf.AgOpenMenuOpen)
                {
                    // A tap elsewhere closes the open pop-out menu, and its labels with it.
                    mf.AgOpenCloseMenus();
                    mainUntil = DateTime.MinValue;
                }
                else if (mf.AgOpenMapTapIsUsed(Cursor.Position))
                {
                    // One of AgOpenGPS's own map buttons (zoom, U-turn, ...): no labels.
                    mainUntil = DateTime.MinValue;
                }
                else
                {
                    mainUntil = DateTime.UtcNow.AddMilliseconds(MainLabelsMs);
                }
            }
            else
            {
                // A button was pressed: its label has done its job.
                mainUntil = DateTime.MinValue;
            }
        }

        private static bool AgOpenPopupOpen()
        {
            foreach (Form form in Application.OpenForms)
            {
                if (form is AgOpenPopup && form.Visible && !form.IsDisposed) return true;
            }
            return false;
        }

        private void Check()
        {
            try
            {
                if (mf.IsDisposed) return;
                CheckMainScreen();
                CheckOtherScreens();
            }
            catch (Exception ex)
            {
                // Labels must never stop AgOpenGPS: note it once and carry on.
                NoteProblem(ex);
            }
        }

        #region Main screen

        private void CheckMainScreen()
        {
            bool onScreen = mf.Visible && mf.WindowState != FormWindowState.Minimized;
            bool menuOpen = onScreen && mf.AgOpenMenuOpen;
            if (menuOpen && !menuWasOpen) mf.AgOpenHoldMenusOpen();
            menuWasOpen = menuOpen;

            List<AgOpenLabelGroup> groups = null;
            if (menuOpen) groups = mf.AgOpenMenuLabelGroups();
            else if (onScreen && mainUntil > DateTime.UtcNow && !AgOpenPopupOpen()) groups = mf.AgOpenMainLabelGroups();

            if (groups != null)
            {
                Rectangle area = mf.RectangleToScreen(mf.ClientRectangle);
                List<AgOpenLabelChip> chips = LayoutGroups(groups, area, OtherControls(groups));
                if (chips.Count > 0)
                {
                    string signature = Signature(area, chips);
                    if (signature != mainShown || fadeStart != DateTime.MinValue)
                    {
                        if (mainOverlay == null || mainOverlay.IsDisposed) mainOverlay = new AgOpenLabelOverlay();
                        mainOverlay.ShowLabels(mf, area, ToOverlay(chips, area));
                        mainShown = signature;
                        fadeStart = DateTime.MinValue;
                    }
                    return;
                }
            }

            // Nothing to show: fade the labels out, or hide them straight away when a menu closed.
            if (mainOverlay == null || mainOverlay.IsDisposed || !mainOverlay.Visible) return;
            if (menuOpen || !onScreen)
            {
                HideMain();
                return;
            }
            if (fadeStart == DateTime.MinValue) fadeStart = DateTime.UtcNow;
            double done = (DateTime.UtcNow - fadeStart).TotalMilliseconds / FadeMs;
            if (done >= 1) HideMain();
            else mainOverlay.Opacity255 = (byte)Math.Max(0, Math.Min(255, 255 * (1 - done)));
        }

        private void HideMain()
        {
            if (mainOverlay != null && !mainOverlay.IsDisposed) mainOverlay.Hide();
            mainShown = "";
            fadeStart = DateTime.MinValue;
        }

        /// <summary>
        /// Lay out the labels for groups of main-screen buttons, in the order given
        /// (earlier groups get the best places): each beside its button on its
        /// group's side, never on another button or label. Everything else on the
        /// screen (section buttons, the field name, the simulator) is kept clear if
        /// there's room; if not, it may be covered for the few seconds the labels
        /// show. A label with no room at all goes across the bottom of its button.
        /// </summary>
        private List<AgOpenLabelChip> LayoutGroups(List<AgOpenLabelGroup> groups, Rectangle bounds, List<Rectangle> others)
        {
            var hard = new List<Rectangle>();
            var soft = new List<Rectangle>(others);
            foreach (AgOpenLabelGroup group in groups)
            {
                foreach (AgOpenLabelTarget target in group.Targets) (group.Soft ? soft : hard).Add(target.Screen);
            }
            Rectangle inside = Rectangle.Inflate(bounds, -4, -4);

            var placed = new List<AgOpenLabelChip>();
            foreach (AgOpenLabelGroup group in groups)
            {
                if (group.Targets.Count == 0) continue;

                // A group's labels never cover its own buttons.
                List<Rectangle> keepOff = hard;
                if (group.Soft)
                {
                    keepOff = new List<Rectangle>(hard);
                    foreach (AgOpenLabelTarget target in group.Targets) keepOff.Add(target.Screen);
                }

                bool row = group.Side == LabelSide.Above || group.Side == LabelSide.Below;
                foreach (AgOpenLabelChip chip in row ? RowChips(group) : ColumnChips(group))
                {
                    bool done = false;
                    foreach (List<Rectangle> avoid in new[] { soft, null })
                    {
                        done = row ? PlaceInRow(chip, inside, keepOff, avoid, placed) : PlaceInColumn(chip, inside, keepOff, avoid, placed);
                        if (done) break;
                    }
                    if (!done)
                    {
                        // No room: across the bottom of its button (not on buttons other
                        // labels may cover, like the simulator's: those go without).
                        if (group.Soft) continue;
                        OnButton(chip);
                    }
                    placed.Add(chip);
                }
            }
            return placed;
        }

        // Labels beside a column of buttons (left or right), lined up on the column's edge.
        private List<AgOpenLabelChip> ColumnChips(AgOpenLabelGroup group)
        {
            var chips = new List<AgOpenLabelChip>();
            bool right = group.Side == LabelSide.Right;
            int edge = right ? group.Targets.Max(t => t.Screen.Right) : group.Targets.Min(t => t.Screen.Left);

            foreach (List<AgOpenLabelTarget> row in Rows(group))
            {
                var words = new List<string>();
                foreach (AgOpenLabelTarget target in row.OrderBy(t => t.Screen.Left))
                {
                    string label = AgOpenButtonNames.Get(AgOpenButtonNames.MainScreen, target.Key, null);
                    if (label.Length > 0) words.Add(label);
                }
                if (words.Count == 0) continue;

                string text = string.Join("  \u00B7  ", words);
                Size size = ChipSize(text, SideFont, 260);
                AgOpenLabelTarget nearest = right ? row.OrderBy(t => t.Screen.Right).Last() : row.OrderBy(t => t.Screen.Left).First();
                Rectangle all = row[0].Screen;
                foreach (AgOpenLabelTarget target in row) all = Rectangle.Union(all, target.Screen);
                int centreY = all.Top + all.Height / 2;
                int x = right ? edge + ColumnGap : edge - ColumnGap - size.Width;
                chips.Add(new AgOpenLabelChip
                {
                    Box = new Rectangle(x, centreY - size.Height / 2, size.Width, size.Height),
                    Text = text,
                    Font = SideFont,
                    Side = group.Side,
                    Tip = right ? new Point(nearest.Screen.Right + 1, centreY) : new Point(nearest.Screen.Left - 1, centreY),
                    Button = all
                });
            }

            // Labels taller than their buttons push each other apart, top to bottom.
            chips.Sort((a, b) => a.Box.Top.CompareTo(b.Box.Top));
            for (int i = 1; i < chips.Count; i++)
            {
                int top = chips[i - 1].Box.Bottom + 3;
                if (chips[i].Box.Top < top) chips[i].Box.Y = top;
            }
            return chips;
        }

        // Labels just above (or below) a row of buttons, centred on their buttons, left to right.
        private List<AgOpenLabelChip> RowChips(AgOpenLabelGroup group)
        {
            var chips = new List<AgOpenLabelChip>();
            bool above = group.Side == LabelSide.Above;
            List<AgOpenLabelTarget> targets = group.Targets.OrderBy(t => t.Screen.Left).ToList();

            // A label may be wider than its button: its neighbours make room.
            int pitch = int.MaxValue;
            for (int i = 1; i < targets.Count; i++)
            {
                int d = Centre(targets[i].Screen) - Centre(targets[i - 1].Screen);
                if (d > 0) pitch = Math.Min(pitch, d);
            }
            if (pitch == int.MaxValue) pitch = 120;
            int widest = Math.Max(64, Math.Min(170, 2 * pitch - 16));

            foreach (AgOpenLabelTarget target in targets)
            {
                string label = AgOpenButtonNames.Get(AgOpenButtonNames.MainScreen, target.Key, null);
                if (label.Length == 0) continue;
                Font font = FittingFont(label, widest);
                Size size = ChipSize(label, font, widest);
                int centreX = Centre(target.Screen);
                int y = above ? target.Screen.Top - RowGap - size.Height : target.Screen.Bottom + RowGap;
                chips.Add(new AgOpenLabelChip
                {
                    Box = new Rectangle(centreX - size.Width / 2, y, size.Width, size.Height),
                    Text = label,
                    Font = font,
                    Side = group.Side,
                    Tip = above ? new Point(centreX, target.Screen.Top - 1) : new Point(centreX, target.Screen.Bottom + 1),
                    Button = target.Screen
                });
            }

            // Neighbours that would touch share the difference: each moves sideways
            // (its point staying on it) so as many as possible fit in one line.
            for (int pass = 0; pass < 40; pass++)
            {
                bool moved = false;
                for (int i = 1; i < chips.Count; i++)
                {
                    AgOpenLabelChip a = chips[i - 1], b = chips[i];
                    int overlap = a.Box.Right + 6 - b.Box.Left;
                    if (overlap <= 0) continue;
                    int aRoom = Math.Max(0, a.Box.X - RowLeftmost(a));
                    int bRoom = Math.Max(0, RowRightmost(b) - b.Box.X);
                    int left = Math.Min(aRoom, (overlap + 1) / 2);
                    int right = Math.Min(bRoom, overlap - left);
                    left = Math.Min(aRoom, overlap - right);
                    if (left == 0 && right == 0) continue;
                    a.Box.X -= left;
                    b.Box.X += right;
                    moved = true;
                }
                if (!moved) break;
            }
            return chips;
        }

        // How far a row label can move sideways with its point still on it.
        private static int RowLeftmost(AgOpenLabelChip chip)
        {
            return Math.Min(chip.Tip.X + 14 - chip.Box.Width, chip.Tip.X - chip.Box.Width / 2);
        }

        private static int RowRightmost(AgOpenLabelChip chip)
        {
            return Math.Max(chip.Tip.X - 14, chip.Tip.X - chip.Box.Width / 2);
        }

        private static int Centre(Rectangle r)
        {
            return r.Left + r.Width / 2;
        }

        // Rows of buttons (only buttons side by side in a menu with JoinRows share one).
        private static List<List<AgOpenLabelTarget>> Rows(AgOpenLabelGroup group)
        {
            var rows = new List<List<AgOpenLabelTarget>>();
            foreach (AgOpenLabelTarget target in group.Targets.OrderBy(t => t.Screen.Top + t.Screen.Height / 2))
            {
                List<AgOpenLabelTarget> last = rows.Count > 0 ? rows[rows.Count - 1] : null;
                int centre = target.Screen.Top + target.Screen.Height / 2;
                if (group.JoinRows && last != null)
                {
                    AgOpenLabelTarget first = last[0];
                    int firstCentre = first.Screen.Top + first.Screen.Height / 2;
                    if (Math.Abs(centre - firstCentre) < Math.Min(first.Screen.Height, target.Screen.Height) / 2)
                    {
                        last.Add(target);
                        continue;
                    }
                }
                rows.Add(new List<AgOpenLabelTarget> { target });
            }
            return rows;
        }

        // A row label: right next to its button (where the spreading put it), moved
        // sideways if something is in the way, or one level further out with a line
        // to its button. Its point always stays on it.
        private static bool PlaceInRow(AgOpenLabelChip chip, Rectangle inside, List<Rectangle> keepOff, List<Rectangle> avoid, List<AgOpenLabelChip> placed)
        {
            Rectangle start = chip.Box;
            int outward = chip.Side == LabelSide.Above ? -1 : 1;
            int leftmost = RowLeftmost(chip), rightmost = RowRightmost(chip);
            for (int level = 0; level < 2; level++)
            {
                int y = start.Y + outward * level * (start.Height + 4);
                int from = level == 0 ? start.X : chip.Tip.X - start.Width / 2; // one level out: centred
                for (int step = 0; ; step++)
                {
                    int dx = (step % 2 == 1 ? -1 : 1) * ((step + 1) / 2) * 3;
                    int x = from + dx;
                    if (x < leftmost && from - Math.Abs(dx) < leftmost && from + Math.Abs(dx) > rightmost) break;
                    if (x < leftmost || x > rightmost) continue;
                    chip.Box = new Rectangle(x, y, start.Width, start.Height);
                    if (Fits(chip, inside, keepOff, avoid, placed)) return true;
                }
            }
            chip.Box = start;
            return false;
        }

        // A column label: beside its button, moved up or down a little (still beside
        // it) if something is in the way, or past other buttons next to the column
        // (e.g. the recorded path buttons), with a line back to its button.
        private static bool PlaceInColumn(AgOpenLabelChip chip, Rectangle inside, List<Rectangle> keepOff, List<Rectangle> avoid, List<AgOpenLabelChip> placed)
        {
            Rectangle start = chip.Box;
            Point tip = chip.Tip;
            var xs = new List<int> { start.X };
            int past = PastButtons(chip, keepOff, avoid);
            if (past != start.X) xs.Add(past);
            foreach (int x in xs)
            {
                for (int step = 0; step <= 48; step++)
                {
                    int dy = (step % 2 == 1 ? -1 : 1) * ((step + 1) / 2) * 2; // 0, -2, +2, ... up to 48 px
                    chip.Box = new Rectangle(x, start.Y + dy, start.Width, start.Height);
                    if (Math.Min(chip.Box.Bottom, chip.Button.Bottom) - Math.Max(chip.Box.Top, chip.Button.Top) < 14) continue;
                    chip.Tip = new Point(tip.X, ColumnTipY(chip));
                    if (Fits(chip, inside, keepOff, avoid, placed)) return true;
                }
            }
            chip.Box = start;
            chip.Tip = tip;
            return false;
        }

        // Where a column label's point meets its button: level with the label, on the button.
        private static int ColumnTipY(AgOpenLabelChip chip)
        {
            int centre = chip.Button.Top + chip.Button.Height / 2;
            int low = Math.Max(chip.Box.Top + 10, chip.Button.Top + 4);
            int high = Math.Min(chip.Box.Bottom - 10, chip.Button.Bottom - 4);
            if (low > high) return (low + high) / 2;
            return Math.Max(low, Math.Min(high, centre));
        }

        // How far out a column label must go to clear the buttons it lands on (at most 200 px).
        private static int PastButtons(AgOpenLabelChip chip, List<Rectangle> keepOff, List<Rectangle> avoid)
        {
            Rectangle box = chip.Box;
            for (int guard = 0; guard < 8; guard++)
            {
                Rectangle hit = Rectangle.Empty;
                foreach (Rectangle r in avoid == null ? keepOff : keepOff.Concat(avoid))
                {
                    if (r != chip.Button && !chip.Button.Contains(r) && r.IntersectsWith(box)) { hit = r; break; }
                }
                if (hit.IsEmpty) break;
                box.X = chip.Side == LabelSide.Right ? hit.Right + ColumnGap : hit.Left - ColumnGap - box.Width;
                if (Math.Abs(box.X - chip.Box.X) > 200) return chip.Box.X;
            }
            return box.X;
        }

        // Inside the window; off other buttons (and, while avoid is given, off
        // everything else, with its line too); off the labels and lines already placed.
        private static bool Fits(AgOpenLabelChip chip, Rectangle inside, List<Rectangle> keepOff, List<Rectangle> avoid, List<AgOpenLabelChip> placed)
        {
            if (!inside.Contains(chip.Box)) return false;
            Rectangle lead = Lead(chip);
            foreach (Rectangle r in keepOff)
            {
                if (r == chip.Button || chip.Button.Contains(r)) continue;
                if (r.IntersectsWith(chip.Box)) return false;
                if (avoid != null && !lead.IsEmpty && r.IntersectsWith(lead)) return false;
            }
            if (avoid != null)
            {
                foreach (Rectangle r in avoid)
                {
                    if (r == chip.Button) continue;
                    if (r.IntersectsWith(chip.Box) || (!lead.IsEmpty && r.IntersectsWith(lead))) return false;
                }
            }
            foreach (AgOpenLabelChip other in placed)
            {
                Rectangle box = Rectangle.Inflate(other.Box, 2, 2);
                if (box.IntersectsWith(chip.Box) || (!lead.IsEmpty && box.IntersectsWith(lead))) return false;
                Rectangle otherLead = Lead(other);
                if (!otherLead.IsEmpty && otherLead.IntersectsWith(chip.Box)) return false;
            }
            return true;
        }

        // The thin line from a label's point on to its button, when the button is
        // further away than the point reaches.
        private static Rectangle Lead(AgOpenLabelChip chip)
        {
            Rectangle b = chip.Box;
            Point t = chip.Tip;
            int reach = AgOpenLabelOverlay.PointerReach;
            switch (chip.Side)
            {
                case LabelSide.Above: return t.Y - b.Bottom > reach ? Rectangle.FromLTRB(t.X - 3, b.Bottom, t.X + 3, t.Y) : Rectangle.Empty;
                case LabelSide.Below: return b.Top - t.Y > reach ? Rectangle.FromLTRB(t.X - 3, t.Y, t.X + 3, b.Top) : Rectangle.Empty;
                case LabelSide.Right: return b.Left - t.X > reach ? Rectangle.FromLTRB(t.X, t.Y - 3, b.Left, t.Y + 3) : Rectangle.Empty;
                case LabelSide.Left: return t.X - b.Right > reach ? Rectangle.FromLTRB(b.Right, t.Y - 3, t.X, t.Y + 3) : Rectangle.Empty;
                default: return Rectangle.Empty;
            }
        }

        // No room beside the button: across the bottom of the button itself.
        private void OnButton(AgOpenLabelChip chip)
        {
            Rectangle b = chip.Button;
            int width = Math.Max(40, b.Width - 4);
            Size size = ChipSize(chip.Text, ScreenFont, width);
            int height = Math.Min(size.Height, Math.Max(22, b.Height * 2 / 3));
            chip.Box = new Rectangle(b.Left + 2, b.Bottom - height - 2, width, height);
            chip.Font = ScreenFont;
            chip.Side = LabelSide.OnButton;
        }

        // Everything else showing on the main screen (section buttons, the field
        // name, the simulator, ...): labels keep off it if they can.
        private List<Rectangle> OtherControls(List<AgOpenLabelGroup> groups)
        {
            var targets = new HashSet<Rectangle>();
            foreach (AgOpenLabelGroup group in groups)
            {
                foreach (AgOpenLabelTarget target in group.Targets) targets.Add(target.Screen);
            }
            var list = new List<Rectangle>();
            AddOthers(mf, targets, list);
            return list;
        }

        private void AddOthers(Control parent, HashSet<Rectangle> targets, List<Rectangle> list)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child == mf.oglMain || child.Width < 4 || child.Height < 4) continue;
                if (child is ToolStrip || child.Name == "oglBack") continue;
                if (child.HasChildren && !(child is ButtonBase) && !(child is ComboBox) && !(child is UpDownBase))
                {
                    AddOthers(child, targets, list);
                    continue;
                }
                Rectangle r = child.RectangleToScreen(child.ClientRectangle);
                if (!targets.Contains(r)) list.Add(r);
            }
        }

        #endregion

        #region Other screens

        // Picture-only buttons on any other open AgOpenGPS screen.
        private void CheckOtherScreens()
        {
            var open = new HashSet<Form>();
            var forms = new List<Form>();
            foreach (Form form in Application.OpenForms) forms.Add(form);

            foreach (Form form in forms)
            {
                if (!Labellable(form)) continue;
                open.Add(form);

                List<AgOpenLabelChip> chips = cleared.Contains(form) ? new List<AgOpenLabelChip>() : ScreenChips(form);
                AgOpenLabelOverlay overlay;
                screenOverlays.TryGetValue(form, out overlay);
                if (chips.Count == 0)
                {
                    if (overlay != null && !overlay.IsDisposed && overlay.Visible) overlay.Hide();
                    screenShown.Remove(form);
                    continue;
                }

                Rectangle area = form.RectangleToScreen(form.ClientRectangle);
                string signature = Signature(area, chips);
                string shown;
                if (overlay != null && !overlay.IsDisposed && overlay.Visible
                    && screenShown.TryGetValue(form, out shown) && shown == signature) continue;

                if (overlay == null || overlay.IsDisposed)
                {
                    overlay = new AgOpenLabelOverlay();
                    screenOverlays[form] = overlay;
                }
                overlay.ShowLabels(form, area, ToOverlay(chips, area));
                screenShown[form] = signature;
            }

            // Screens that closed: their labels go with them.
            foreach (Form gone in screenOverlays.Keys.Where(f => !open.Contains(f)).ToList())
            {
                AgOpenLabelOverlay overlay = screenOverlays[gone];
                if (!overlay.IsDisposed) overlay.Close();
                screenOverlays.Remove(gone);
                screenShown.Remove(gone);
                cleared.Remove(gone);
            }
        }

        private bool Labellable(Form form)
        {
            if (form == null || form == mf || form.IsDisposed || !form.Visible) return false;
            if (form is AgOpenPopup || form is AgOpenLabelOverlay) return false;
            if (form.WindowState == FormWindowState.Minimized) return false;
            return !SkipScreens.Contains(form.GetType().Name);
        }

        // Labels beside each picture-only button, on whichever side has room: below,
        // above, right or left. With no room anywhere, over the bottom of the button.
        private List<AgOpenLabelChip> ScreenChips(Form form)
        {
            var chips = new List<AgOpenLabelChip>();
            var buttons = new List<Control>();
            var obstacles = new List<KeyValuePair<Control, Rectangle>>();
            Collect(form, buttons, obstacles);
            if (buttons.Count == 0) return chips;

            Rectangle area = form.RectangleToScreen(form.ClientRectangle);
            string screen = form.GetType().Name;
            foreach (Control button in buttons)
            {
                string label = AgOpenButtonNames.Get(screen, button.Name, form.Text);
                if (label.Length == 0) continue;

                Rectangle b = button.RectangleToScreen(button.ClientRectangle);
                int width = Math.Max(90, Math.Min(180, b.Width + 30));
                Size size = ChipSize(label, ScreenFont, width);
                AgOpenLabelChip chip = null;
                foreach (LabelSide side in new[] { LabelSide.Below, LabelSide.Above, LabelSide.Right, LabelSide.Left })
                {
                    Rectangle box = Beside(b, size, side);
                    if (!area.Contains(box) || Covers(box, button, obstacles) || chips.Any(c => c.Box.IntersectsWith(box))) continue;
                    chip = new AgOpenLabelChip { Box = box, Text = label, Font = ScreenFont, Side = side, Tip = TipFor(b, side) };
                    break;
                }
                if (chip == null)
                {
                    // No room beside it: across the bottom of the button itself.
                    Size inside = ChipSize(label, ScreenFont, Math.Max(40, b.Width - 4));
                    int height = Math.Min(inside.Height, Math.Max(20, b.Height / 2));
                    chip = new AgOpenLabelChip
                    {
                        Box = new Rectangle(b.Left + 2, b.Bottom - height - 2, Math.Max(40, b.Width - 4), height),
                        Text = label,
                        Font = ScreenFont,
                        Side = LabelSide.OnButton
                    };
                }
                chips.Add(chip);
            }
            return chips;
        }

        // The buttons worth labelling (picture only, big enough) and everything a label mustn't cover.
        private static void Collect(Control parent, List<Control> buttons, List<KeyValuePair<Control, Rectangle>> obstacles)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Visible || child.Width < 4 || child.Height < 4) continue;
                bool container = child.HasChildren && !(child is ButtonBase) && !(child is ComboBox) && !(child is NumericUpDown);
                if (container)
                {
                    Collect(child, buttons, obstacles);
                    continue;
                }

                // Big drawing areas (the field view) can carry labels.
                string type = child.GetType().Name;
                if (type != "GLControl" && !(child is PictureBox))
                {
                    obstacles.Add(new KeyValuePair<Control, Rectangle>(child, child.RectangleToScreen(child.ClientRectangle)));
                }

                var button = child as ButtonBase;
                if (button == null || string.IsNullOrEmpty(button.Name)) continue;
                if (button.Width < 36 || button.Height < 30) continue;
                bool picture = button.Image != null || button.BackgroundImage != null;
                if (picture && string.IsNullOrWhiteSpace(button.Text)) buttons.Add(button);
            }
        }

        private static bool Covers(Rectangle box, Control own, List<KeyValuePair<Control, Rectangle>> obstacles)
        {
            foreach (KeyValuePair<Control, Rectangle> obstacle in obstacles)
            {
                if (obstacle.Key != own && obstacle.Value.IntersectsWith(box)) return true;
            }
            return false;
        }

        private static Rectangle Beside(Rectangle b, Size size, LabelSide side)
        {
            int centreX = b.Left + b.Width / 2, centreY = b.Top + b.Height / 2;
            const int gap = 8;
            switch (side)
            {
                case LabelSide.Below: return new Rectangle(centreX - size.Width / 2, b.Bottom + gap, size.Width, size.Height);
                case LabelSide.Above: return new Rectangle(centreX - size.Width / 2, b.Top - gap - size.Height, size.Width, size.Height);
                case LabelSide.Right: return new Rectangle(b.Right + gap, centreY - size.Height / 2, size.Width, size.Height);
                default: return new Rectangle(b.Left - gap - size.Width, centreY - size.Height / 2, size.Width, size.Height);
            }
        }

        private static Point TipFor(Rectangle b, LabelSide side)
        {
            int centreX = b.Left + b.Width / 2, centreY = b.Top + b.Height / 2;
            switch (side)
            {
                case LabelSide.Below: return new Point(centreX, b.Bottom + 1);
                case LabelSide.Above: return new Point(centreX, b.Top - 1);
                case LabelSide.Right: return new Point(b.Right + 1, centreY);
                default: return new Point(b.Left - 1, centreY);
            }
        }

        #endregion

        #region Measuring

        // The size of a label box: its words wrapped to at most this width, plus a margin.
        private Size ChipSize(string text, Font font, int maxWidth)
        {
            string key = font.Size + "|" + maxWidth + "|" + text;
            Size size;
            if (measured.TryGetValue(key, out size)) return size;
            Size words = AgOpenPopup.MeasureText(measure, text, font, Math.Max(10, maxWidth - 12), true);
            size = new Size(Math.Min(maxWidth, words.Width + 14), Math.Max(28, words.Height + 8));
            if (measured.Count > 2000) measured.Clear();
            measured[key] = size;
            return size;
        }

        // The row font, smaller if a single word is too wide for the space.
        private Font FittingFont(string text, int width)
        {
            foreach (string word in text.Split(' '))
            {
                string key = "word|" + word;
                Size size;
                if (!measured.TryGetValue(key, out size))
                {
                    size = AgOpenPopup.MeasureText(measure, word, RowFont, 1000, false);
                    measured[key] = size;
                }
                if (size.Width > width - 12) return ScreenFont;
            }
            return RowFont;
        }

        private static List<AgOpenLabelChip> ToOverlay(List<AgOpenLabelChip> chips, Rectangle area)
        {
            var list = new List<AgOpenLabelChip>(chips.Count);
            foreach (AgOpenLabelChip chip in chips)
            {
                list.Add(new AgOpenLabelChip
                {
                    Box = new Rectangle(chip.Box.X - area.X, chip.Box.Y - area.Y, chip.Box.Width, chip.Box.Height),
                    Text = chip.Text,
                    Font = chip.Font,
                    Side = chip.Side,
                    Tip = new Point(chip.Tip.X - area.X, chip.Tip.Y - area.Y),
                    Button = new Rectangle(chip.Button.X - area.X, chip.Button.Y - area.Y, chip.Button.Width, chip.Button.Height)
                });
            }
            return list;
        }

        private static string Signature(Rectangle area, List<AgOpenLabelChip> chips)
        {
            var text = new System.Text.StringBuilder();
            text.Append(area.ToString());
            foreach (AgOpenLabelChip chip in chips)
            {
                text.Append('|').Append(chip.Box.ToString()).Append(chip.Text).Append(chip.Tip.ToString());
            }
            return text.ToString();
        }

        #endregion

        private void NoteProblem(Exception ex)
        {
            if (problemLogged) return;
            problemLogged = true;
            Log.EventWriter("[AgOpen] Button labels problem: " + ex);
        }
    }
}
