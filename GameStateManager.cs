using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading.Tasks;

namespace PlayniteGameOverlay
{
    public class GameStateManager
    {
        private readonly Logger _logger;

        public GameStateManager(Logger logger)
        {
            _logger = logger;
        }

        public Process FindProcessById(int processId)
        {
            try
            {
                return Process.GetProcessById(processId);
            }
            catch
            {
                return null;
            }
        }

        public void ReturnToGame(GameOverlayData gameData)
        {
            if (gameData != null)
            {
                var proc = FindProcessById(gameData.ProcessId);
                if (proc != null)
                {
                    // Restore the window if it's minimized
                    if (WindowHelper.IsIconic(proc.MainWindowHandle))
                    {
                        WindowHelper.ShowWindow(proc.MainWindowHandle, WindowHelper.SW_RESTORE);
                    }
                    // Bring the game window to the front
                    WindowHelper.SetForegroundWindow(proc.MainWindowHandle);
                }
            }
        }

        /// <summary>
        /// Closes the game. CloseAndEnd and EndTask kill the whole process tree at once, with no
        /// graceful-close wait, so games that are slow to shut down end immediately.
        /// CloseWindow only asks the main window to close (falls back to Kill if that fails).
        /// <paramref name="extraPids"/> are additional roots to kill (for example the process Playnite started).
        /// </summary>
        public void CloseGame(GameOverlayData gameData, CloseBehavior behavior = CloseBehavior.CloseAndEnd, IEnumerable<int> extraPids = null)
        {
            if (gameData == null)
                return;

            var roots = new List<int>();
            if (gameData.ProcessId > 0)
                roots.Add(gameData.ProcessId);
            if (extraPids != null)
                roots.AddRange(extraPids.Where(p => p > 0));
            roots = roots.Distinct().ToList();

            switch (behavior)
            {
                case CloseBehavior.CloseWindow:
                    var proc = FindProcessById(gameData.ProcessId);
                    if (proc == null)
                        return;
                    if (!proc.CloseMainWindow())
                        KillProcessTree(proc.Id);
                    proc.Close();
                    break;
                case CloseBehavior.CloseAndEnd:
                case CloseBehavior.EndTask:
                    foreach (var pid in roots)
                        KillProcessTree(pid);
                    break;
                default:
                    _logger.Log($"Unknown close behavior: {behavior}", "ERROR");
                    break;
            }
        }

        /// <summary>Force-kills a process and all of its children (taskkill /F /T), falling back to Process.Kill.</summary>
        private void KillProcessTree(int pid)
        {
            if (pid <= 0 || !GameProcessSuspender.IsAlive(pid))
                return;

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = "taskkill",
                    Arguments = $"/F /T /PID {pid}",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                using (var killer = Process.Start(psi))
                {
                    // taskkill returns almost instantly; do not block the UI for long if it hangs
                    if (killer != null && !killer.WaitForExit(2000))
                        _logger.Log($"taskkill for PID {pid} is taking long; continuing.", "WARNING");
                }
            }
            catch (Exception ex)
            {
                _logger.Log($"taskkill failed for PID {pid} ({ex.Message}); using Process.Kill.", "WARNING");
            }

            if (GameProcessSuspender.IsAlive(pid))
            {
                try
                {
                    using (var p = Process.GetProcessById(pid))
                        p.Kill();
                }
                catch (Exception ex)
                {
                    _logger.Log($"Could not kill PID {pid}: {ex.Message}", "ERROR");
                }
            }
        }

        public string FormatPlaytime(TimeSpan playtime)
        {
            if (playtime.TotalMinutes < 120)
                return $"{(int)playtime.TotalMinutes} mins.";

            return $"{(int)playtime.TotalHours} hrs.";
        }

        public string GetFormattedSessionTime(DateTime gameStartTime)
        {
            var playTime = DateTime.Now - gameStartTime;
            return $"{playTime.Hours}:{playTime.Minutes:D2}:{playTime.Seconds:D2}";
        }

        public AchievementInfo GetMostRecentAchievement(GameOverlayData gameData)
        {
            if (gameData?.Achievements == null || gameData.Achievements.Count == 0)
                return null;

            return new AchievementInfo
            {
                MostRecent = gameData.Achievements.FindAll(a => a.IsUnlocked).OrderByDescending(a => a.UnlockDate).FirstOrDefault(),
                TotalCount = gameData.Achievements.Count,
                UnlockedCount = gameData.Achievements.Count(a => a.IsUnlocked)
            };
        }
    }

    public class AchievementInfo
    {
        public AchievementData MostRecent { get; set; }
        public int TotalCount { get; set; }
        public int UnlockedCount { get; set; }
    }
}