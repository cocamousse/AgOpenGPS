using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;
using AgLibrary.Logging;
using AgOpenGPS.AgOpen;

namespace AgOpenGPS
{
    /// <summary>
    /// Button labels (the labels version): tells AgOpenButtonLabels where the main
    /// screen's buttons and pop-out menus are. Kept in this file, plus one call in
    /// InitAgOpenHub, so it's easy to leave out or merge.
    /// </summary>
    public partial class FormGPS
    {
        private AgOpenButtonLabels agOpenLabels;

        private void InitAgOpenLabels()
        {
            try
            {
                agOpenLabels = new AgOpenButtonLabels(this);
                agOpenLabels.Start();
                FormClosing += (s, e) =>
                {
                    // AgOpenGPS cancels the first close to save the field, then closes again.
                    if (!e.Cancel) agOpenLabels.Stop();
                };
            }
            catch (Exception ex)
            {
                Log.EventWriter("[AgOpen] Button labels couldn't start: " + ex);
            }
        }

        /// <summary>The Tracks or View pop-out menu is open.</summary>
        internal bool AgOpenMenuOpen
        {
            get { return flp1.Visible || panelNavigation.Visible; }
        }

        /// <summary>A tap on the map closes the open pop-out menu.</summary>
        internal void AgOpenCloseMenus()
        {
            if (flp1.Visible) flp1.Visible = false;
            if (panelNavigation.Visible) panelNavigation.Visible = false;
        }

        /// <summary>
        /// A pop-out menu just opened: give it about 10 seconds before it closes by
        /// itself (stock AgOpenGPS: about 4), so there's time to read its labels.
        /// </summary>
        internal void AgOpenHoldMenusOpen()
        {
            if (flp1.Visible) trackMethodPanelCounter = 10;   // counted down once a second
            if (panelNavigation.Visible) navPanelCounter = 5; // counted down every 2 seconds
        }

        /// <summary>
        /// The main screen's buttons, grouped by where their labels go, in the order
        /// they get the best places: left of the right column (auto steer first),
        /// above the bottom row, below the top-right buttons, right of the left
        /// column (with the menu and GO), and beside the recorded path and simulator
        /// buttons when they're showing.
        /// </summary>
        internal List<AgOpenLabelGroup> AgOpenMainLabelGroups()
        {
            var right = new AgOpenLabelGroup(LabelSide.Left);
            right.AddButtonsIn(panelRight);

            var bottom = new AgOpenLabelGroup(LabelSide.Above);
            bottom.AddButtonsIn(panelBottom);

            var topRight = new AgOpenLabelGroup(LabelSide.Below);
            topRight.AddButtonsIn(panelControlBox);

            var left = new AgOpenLabelGroup(LabelSide.Right);
            left.AddItemsIn(menuStrip1);
            left.AddButtonsIn(panelLeft);
            left.AddItemsIn(statusStripLeft);
            left.AddItemsIn(statusStrip2);
            left.AddItemsIn(statusStrip1);
            left.Add(agOpenGoButton);

            var paths = new AgOpenLabelGroup(LabelSide.Right);
            paths.AddButtonsIn(panelDrag);

            // The simulator's buttons sit just above the bottom row: the bottom row's
            // labels may cover them when there's no other room.
            var simulator = new AgOpenLabelGroup(LabelSide.Above, soft: true);
            simulator.AddButtonsIn(panelSim);

            return new List<AgOpenLabelGroup> { right, bottom, topRight, left, paths, simulator };
        }

        /// <summary>
        /// The tap is on one of AgOpenGPS's own map buttons (zoom, U-turn, lateral,
        /// pan, hide the bottom row, tram override, reset direction, picking a
        /// flag): it does that, and doesn't bring up the labels. These are the same
        /// zones oglMain_MouseDown uses.
        /// </summary>
        internal bool AgOpenMapTapIsUsed(Point screenPoint)
        {
            Point point = oglMain.PointToClient(screenPoint);
            int width = oglMain.Width, height = oglMain.Height;
            int centerX = width / 2, centerY = height / 2;

            if (isJobStarted)
            {
                if ((isBtnAutoSteerOn || yt.isYouTurnBtnOn) && trk.idx > -1)
                {
                    if (point.Y > 90 && point.Y < 150)
                    {
                        int uTurn = width / 2 + width / 5; // swap U-turn direction, K-turn or U-turn
                        if (point.X > uTurn - 80 && point.X < uTurn + 80) return true;
                        int manual = width / 2 - width / 4; // manual U-turn left or right
                        if (!isStanleyUsed && isUTurnOn && point.X > manual - 100 && point.X < manual + 100) return true;
                    }
                    if (point.Y > 170 && point.Y < 240)
                    {
                        int lateral = width / 2 - width / 4;
                        if (isLateralOn && point.X > lateral - 100 && point.X < lateral + 100) return true;
                    }
                }

                // Hide or show the bottom row.
                if (point.X > 30 && point.X < 60 && point.Y > height - 60 && point.Y < height - 30) return true;

                // Tram override, left and right.
                int bottomSide = height / 5 + 25;
                int fromCentre = Math.Abs(point.X - centerX);
                if (tool.isDisplayTramControl && point.Y > bottomSide - 50 && point.Y < bottomSide
                    && fromCentre > 20 && fromCentre < 100) return true;
            }

            // Pan.
            if (point.X > 30 && point.X < 60 && point.Y > 50 && point.Y < 80) return true;

            // Zoom in and out, at the right of the map.
            if (point.X > width - 80 && ((point.Y > 30 && point.Y < 120) || (point.Y > 170 && point.Y < 260))) return true;

            // Reset direction: a tap on the tractor.
            if (headingFromSource != "Dual" && Math.Abs(point.X - centerX) < 40 && Math.Abs(point.Y - centerY) < 60) return true;

            // Picking a flag on the map.
            return Application.OpenForms["FormFlags"] != null;
        }

        /// <summary>The open pop-out menu's buttons: Tracks (labels on its left), View (on its right, a row at a time).</summary>
        internal List<AgOpenLabelGroup> AgOpenMenuLabelGroups()
        {
            var groups = new List<AgOpenLabelGroup>();
            if (flp1.Visible)
            {
                var tracks = new AgOpenLabelGroup(LabelSide.Left);
                tracks.AddButtonsIn(flp1);
                groups.Add(tracks);
            }
            if (panelNavigation.Visible)
            {
                var view = new AgOpenLabelGroup(LabelSide.Right, joinRows: true);
                view.AddButtonsIn(panelNavigation);
                groups.Add(view);
            }
            return groups;
        }
    }
}
