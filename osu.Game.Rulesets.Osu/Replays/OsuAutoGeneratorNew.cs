// Copyright (c) 2007-2018 ppy Pty Ltd <contact@ppy.sh>.
// Licensed under the MIT Licence - https://raw.githubusercontent.com/ppy/osu/master/LICENCE

using OpenTK;
using osu.Framework.MathUtils;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Osu.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Diagnostics;
using osu.Framework.Graphics;
using osu.Game.Rulesets.Objects.Types;
using osu.Game.Rulesets.Replays;
using osu.Game.Rulesets.Scoring;


namespace osu.Game.Rulesets.Osu.Replays
{
    public class OsuAutoGeneratorNew : OsuAutoGeneratorBase
    {
        // Hitpoint types
        private enum HT {
            Circle,
            SliderHead,
            SliderTick,
            SliderTail,
            SpinnerStart,
            SpinnerEnd
        }

        // Click types
        private enum CT {
            Nop,
            Click,
            Hold,
            Release,
        }
        private class Hitpoint
        {
            public double Time;

            public HT Type;

            public Vector2 Position;

            // 0 - no preference
            // 1 - click
            // 2 - hold (or click+hold)
            // 3 - release
            public CT LMB;
            public CT RMB;

            public Hitpoint(double time, HT type, Vector2 position)
            {
                Time = time;
                Type = type;
                Position = position;
            }
        }

        private class Hitpoints : SortedDictionary<double, List<Hitpoint>>
        {
        }

        #region Parameters

        public double alternating_threshold = 0.5; // Time in seconds between two keystrokes before we switch from alternating to singletapping

        /// <summary>
        /// If delayed movements should be used, causing the cursor to stay on each hitobject for as long as possible.
        /// Mainly for Autopilot.
        /// </summary>
        public bool DelayedMovements; // ModManager.CheckActive(Mods.Relax2);

        #endregion

        #region Constants

        /// <summary>
        /// The "reaction time" in ms between "seeing" a new hit object and moving to "react" to it.
        /// </summary>
        private readonly double reactionTime;

        /// <summary>
        /// What easing to use when moving between hitobjects
        /// </summary>
        private Easing preferredEasing => DelayedMovements ? Easing.InOutCubic : Easing.Out;

        #endregion

        #region Construction / Initialisation

        public OsuAutoGeneratorNew(Beatmap<OsuHitObject> beatmap)
            : base(beatmap)
        {
            // Already superhuman, but still somewhat realistic
            reactionTime = ApplyModsToRate(100);
        }

        #endregion

        #region Generator

        // Used throughout the generation process
        private Hitpoints hitpoints;
        private int num_active_spinners = 0;
        private int num_active_sliders = 0;
        private double lmb_last_clicked = double.MinValue;
        private double rmb_last_clicked = double.MinValue;

        private bool holdZone => num_active_sliders > 0 || num_active_spinners > 0;
        private bool lmbCurrentlyHeld = false;
        private bool rmbCurrentlyHeld = false;

        public override Replay Generate()
        {
            AddFrameToReplay(new ReplayFrame(-100000, 256, 500, ReplayButtonState.None));
            AddFrameToReplay(new ReplayFrame(Beatmap.HitObjects[0].StartTime - 1500, 256, 500, ReplayButtonState.None));
            AddFrameToReplay(new ReplayFrame(Beatmap.HitObjects[0].StartTime - 1000, 256, 192, ReplayButtonState.None));

            // 1) Get a list of all points that we need to hit,
            //    sorted by time, along with their type (circle, sliderhead/tick/tail)
            // 2) Assign buttons to every frame and how long they should be held
            // 3) Go through hitpoints chronologically,
            //    interpolating between hitpoints with preferredEasing

            // 1) Get list of hitpoints. Each hitpoint is a tuple of its type and its position.
            calculateHitpoints();

            // 2) Assign buttons
            calculateButtons();

            // 3) Go through hitpoints chronologically and add frames
            foreach (KeyValuePair<double, List<Hitpoint>> kvp in hitpoints)
            {
                //double t = kvp.Key;

                // First do the releases
                foreach (Hitpoint h in kvp.Value)
                {
                    if (h.LMB == CT.Release)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState & ~ReplayButtonState.Left1));
                        break;
                    }
                    else if (h.RMB == CT.Release)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState & ~ReplayButtonState.Right1));
                        break;
                    }
                }

                // If there's any hitpoint where we need to click, do it and move on to the next timestamp.
                foreach (Hitpoint h in kvp.Value)
                {
                    if (h.LMB == CT.Click)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time - 50, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState | ReplayButtonState.Left1));
                        AddFrameToReplay(new ReplayFrame(h.Time + KEY_UP_DELAY, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        goto NextTimestep;
                    }
                    else if (h.RMB == CT.Click)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time - 50, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState | ReplayButtonState.Right1));
                        AddFrameToReplay(new ReplayFrame(h.Time + KEY_UP_DELAY, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        goto NextTimestep;
                    }
                }

                // Now for the holds, similarly, go to next timestep if there is a hold.
                foreach (Hitpoint h in kvp.Value)
                {
                    if (h.LMB == CT.Hold)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time - 50, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState | ReplayButtonState.Left1));
                        goto NextTimestep;
                    }
                    else if (h.RMB == CT.Click)
                    {
                        ReplayFrame lastFrame = Frames[Frames.Count - 1];
                        AddFrameToReplay(new ReplayFrame(h.Time - 50, h.Position[0], h.Position[1], lastFrame.ButtonState));
                        AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState | ReplayButtonState.Right1));
                        goto NextTimestep;
                    }
                }

                // Since we didn't have any clicks or holds, just move to any hitpoint (the first one)

                {
                    Hitpoint h = kvp.Value[0];
                    ReplayFrame lastFrame = Frames[Frames.Count - 1];
                    AddFrameToReplay(new ReplayFrame(h.Time, h.Position[0], h.Position[1], lastFrame.ButtonState));
                }

            NextTimestep:

                {}

            }

            return Replay;
        }

        private void calculateButtons()
        {
            foreach (KeyValuePair<double, List<Hitpoint>> kvp in hitpoints)
            {
                // We have to be careful about this because every Hitpoint in the list has exactly the same timestamp.
                // Happens quite often on 2B maps, e.g. a slider tick and a hit circle often appear on the same timestamp.
                // That means we need to prioritise hitobjects that need to be clicked
                //double t = kvp.Key;

                // Firstly update holdZone
                // Add holds before removing them so that release doesn't get called prematurely
                foreach (Hitpoint h in kvp.Value)
                {
                    switch (h.Type)
                    {
                        case HT.SliderHead: // Slider head
                        case HT.SpinnerStart: // Spinner start
                            num_active_spinners++;
                            break;
                    }
                }
                foreach (Hitpoint h in kvp.Value)
                {
                    switch (h.Type)
                    {
                        case HT.SliderTail: // Slider end
                        case HT.SpinnerEnd: // Spinner end
                            num_active_spinners--;
                            release(h);
                            break;
                    }
                }

                // Keep track if we've clicked this frame yet
                bool clicked = false;

                // If there is a slider, click it
                foreach (Hitpoint h in kvp.Value)
                {
                    if (h.Type is HT.SliderHead)
                    {
                        click(h, true);
                        clicked = true;
                        break;
                    }
                }

                // If we haven't used our click yet, try to click a circle
                if (clicked == false)
                {
                    foreach (Hitpoint h in kvp.Value)
                    {
                        if (h.Type is HT.Circle)
                        {
                            click(h);
                            clicked = true;
                            break;
                        }
                    }
                }

                // If we're not already holding, hold any spinners
                if (!lmbCurrentlyHeld && !rmbCurrentlyHeld && holdZone)
                {
                    foreach (Hitpoint h in kvp.Value)
                    {
                        if (h.Type is HT.SpinnerStart)
                        {
                            click(h);
                            clicked = true;
                            break;
                        }
                    }
                }
            }
        }

        private void release(Hitpoint h)
        {
            if (!holdZone)
            {
                // Release the currently held button if no longer in a hold zone
                if (lmbCurrentlyHeld)
                {
                    lmb_last_clicked = h.Time + KEY_UP_DELAY;
                    h.LMB = CT.Release;
                    lmbCurrentlyHeld = false;
                }
                else if (rmbCurrentlyHeld)
                {
                    rmb_last_clicked = h.Time + KEY_UP_DELAY;
                    h.RMB = CT.Release;
                    rmbCurrentlyHeld = false;
                }
                else
                {
                    throw new Exception();
                }
            }
        }

        private void click(Hitpoint h, bool rehold = false)
        {
            bool lmb;

            if (lmbCurrentlyHeld) {
                // LMB currently holding
                // Force RMB click
                lmb = false;

            }
            else if (h.Time - lmb_last_clicked > alternating_threshold || rmbCurrentlyHeld)
            {
                // Singletap or
                // Hold zone with RMB: force LMB singletapping anyway
                lmb = true;

            }
            else if (lmb_last_clicked > rmb_last_clicked)
            {
                // Neither buttons are being held but LMB was recently clicked
                // Use the less recently clicked button
                lmb = false;
            }
            else
            {
                lmb = true;
            }

            CT ct = rehold ? CT.Hold : CT.Click;
            double last_clicked = rehold ? double.NaN : h.Time + KEY_UP_DELAY;

            if (lmb)
            {
                if (rehold)
                {
                    if (rmbCurrentlyHeld)
                    {
                        // Release OTHER button's hold when engaging a hold so we never have 2 holds at once
                        rmb_last_clicked = h.Time + KEY_UP_DELAY;
                        rmbCurrentlyHeld = false;
                        h.RMB = CT.Release;
                    }
                    lmb_last_clicked = double.NaN;
                    lmbCurrentlyHeld = true;
                    h.LMB = CT.Hold;
                }
                else
                {
                    if (holdZone && !rmbCurrentlyHeld)
                    {
                        lmb_last_clicked = double.NaN;
                        lmbCurrentlyHeld = true;
                        h.LMB = CT.Hold;
                    }
                    else
                    {
                        lmb_last_clicked = h.Time + KEY_UP_DELAY;
                        lmbCurrentlyHeld = false;
                        h.LMB = CT.Click;
                    }
                }
            }
            else
            {
                if (rehold)
                {
                    if (lmbCurrentlyHeld)
                    {
                        // Release OTHER button's hold when engaging a hold so we never have 2 holds at once
                        lmb_last_clicked = h.Time + KEY_UP_DELAY;
                        lmbCurrentlyHeld = false;
                        h.LMB = CT.Release;
                    }
                    rmb_last_clicked = double.NaN;
                    rmbCurrentlyHeld = true;
                    h.RMB = CT.Hold;
                }
                else
                {
                    if (holdZone && !lmbCurrentlyHeld)
                    {
                        rmb_last_clicked = double.NaN;
                        rmbCurrentlyHeld = true;
                        h.RMB = CT.Hold;
                    }
                    else
                    {
                        rmb_last_clicked = h.Time + KEY_UP_DELAY;
                        rmbCurrentlyHeld = false;
                        h.RMB = CT.Click;
                    }
                }
            }
        }

        private void addHitpoint(double key, Hitpoint item)
        {
            if (hitpoints.ContainsKey(key))
            {
                hitpoints[key].Add(item);
            }
            else
            {
                hitpoints.Add(key, new List<Hitpoint>{item});
            }
        }

        private void calculateHitpoints() {
            hitpoints = new Hitpoints();

            foreach (OsuHitObject h in Beatmap.HitObjects)
            {
                if (h is HitCircle)
                {
                    addHitpoint(h.StartTime, new Hitpoint(h.StartTime, HT.Circle, h.StackedPosition));
                }
                else if (h is Slider)
                {
                    // Slider head
                    addHitpoint(h.StartTime, new Hitpoint(h.StartTime, HT.SliderHead, h.StackedPosition));

                    // Slider ticks and repeats
                    foreach (OsuHitObject n in h.NestedHitObjects.OfType<SliderTick>())
                    {
                        addHitpoint(n.StartTime, new Hitpoint(n.StartTime, HT.SliderTick, n.StackedPosition));
                    }
                    foreach (OsuHitObject n in h.NestedHitObjects.OfType<RepeatPoint>())
                    {
                        addHitpoint(n.StartTime, new Hitpoint(n.StartTime, HT.SliderTick, n.StackedPosition));
                    }

                    // Slider tail
                    addHitpoint((h as Slider).EndTime, new Hitpoint((h as Slider).EndTime, HT.SliderTail, h.StackedEndPosition));
                }
                else
                {
                    // Spinner start and end
                    addHitpoint(h.StartTime, new Hitpoint(h.StartTime, HT.SpinnerStart, h.StackedPosition));
                    addHitpoint((h as Spinner).EndTime, new Hitpoint((h as Spinner).EndTime, HT.SpinnerEnd, h.StackedEndPosition));
                }
            }
        }

        #endregion

        #region Helper subroutines

        #endregion
    }
}
