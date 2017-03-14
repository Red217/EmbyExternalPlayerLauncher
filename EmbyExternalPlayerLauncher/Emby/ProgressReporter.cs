﻿/*
 *  Copyright © 2017 Andrei K.
 *  
 *  This file is part of Emby External Player Launcher.
 *
 *  Emby External Player Launcher is free software: you can redistribute it and/or modify
 *  it under the terms of the GNU General Public License as published by
 *  the Free Software Foundation, either version 3 of the License, or
 *  (at your option) any later version.
 *
 *  Emby External Player Launcher is distributed in the hope that it will be useful,
 *  but WITHOUT ANY WARRANTY; without even the implied warranty of
 *  MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
 *  GNU General Public License for more details.
 *
 *  You should have received a copy of the GNU General Public License
 *  along with Emby External Player Launcher.  If not, see <http://www.gnu.org/licenses/>.
 */

using EmbyExternalPlayerLauncher.Players;
using log4net;
using MediaBrowser.Model.ApiClient;
using MediaBrowser.Model.Session;
using System;
using System.Diagnostics;
using System.Threading;

namespace EmbyExternalPlayerLauncher.Emby
{
    public class ProgressReporter
    {
        private static readonly ILog log = LogManager.GetLogger(System.Reflection.MethodBase.GetCurrentMethod().DeclaringType);

        private IApiClient embyClient;
        private IPlayerAdapter player;
        private int reportPeriod;
        private string itemId;

        private Thread reporterThread;
        private bool stop = false;
        private object monitor = new object();

        public ProgressReporter(IApiClient embyClient, IPlayerAdapter player, int reportPeriod, string itemId)
        {
            this.embyClient = embyClient;
            this.player = player;
            this.reportPeriod = reportPeriod;
            this.itemId = itemId;

            log.Debug("Creating and starting ProgressReporter.");
            reporterThread = new Thread(new ThreadStart(ReportEmbyStatus));
            reporterThread.Start();
        }

        public void Stop()
        {
            log.Debug("ProgressReporter has been requested to stop.");
            lock (monitor)
            {
                stop = true;
                Monitor.Pulse(monitor);
            }
        }

        private void ReportEmbyStatus()
        {
            //used to report position on stop - by the time the reporter polls, the player might have closed already
            long lastPosition = 0;
            //when MPC-HC is loading a file, its state is None;
            //This flag prevents the reporter from terminating before the player loads the file
            bool startedPlaying = false;

            var stopwatch = new Stopwatch();
            log.Debug("ProgressReporter thread starting");
            for (;;)
            {
                stopwatch.Start();
                var status = player.GetPlayerStatus();

                if (!startedPlaying)
                {
                    startedPlaying = status.State != PlayerState.None;
                }
                else
                {
                    if (status.State == PlayerState.Stopped)
                    {
                        log.Info("MPC-HC is no longer playing");
                        var stopInfo = new PlaybackStopInfo
                        {
                            ItemId = itemId,
                            PositionTicks = (status.Position == 0 ? lastPosition : status.Position) * Utils.EmbyTicksPerMs
                        };
                        Utils.IgnoreExceptions(() => embyClient.ReportPlaybackStoppedAsync(stopInfo).Wait());
                        OnPlayerStopped(EventArgs.Empty);
                        log.Info("Progress reporter terminating");
                        return;
                    }

                    var progressInfo = new PlaybackProgressInfo
                    {
                        CanSeek = player.CanSeek(),
                        IsPaused = status.State == PlayerState.Paused,
                        IsMuted = status.Muted,
                        ItemId = itemId,
                        PlayMethod = PlayMethod.DirectPlay,
                        PositionTicks = status.Position * Utils.EmbyTicksPerMs,
                        VolumeLevel = status.VolumeLevel,
                        RepeatMode = RepeatMode.RepeatNone
                    };
                    Utils.IgnoreExceptions(() => embyClient.ReportPlaybackProgressAsync(progressInfo).Wait());
                    lastPosition = status.Position;
                }

                stopwatch.Stop();
                int sleepTime = reportPeriod - (int)stopwatch.ElapsedMilliseconds;
                log.DebugFormat("Status report took {0}ms.", stopwatch.ElapsedMilliseconds);
                lock (monitor)
                {
                    if (stop)
                        break;
                    if (sleepTime > 0)
                        Monitor.Wait(monitor, sleepTime);
                    if (stop)
                        break;
                }
                stopwatch.Reset();
            }
        }

        #region Custom Events

        public event EventHandler PlayerStopped;

        protected virtual void OnPlayerStopped(EventArgs e)
        {
            PlayerStopped?.Invoke(this, e);
        }

        #endregion
    }
}
