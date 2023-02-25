using log4net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;

namespace FunscriptToolbox
{
    internal static class ApplicationExitHandler
    {
        private static readonly ILog rs_log = LogManager.GetLogger(MethodBase.GetCurrentMethod().DeclaringType);

        private delegate bool EventHandler(CtrlType sig);
        private static EventHandler rs_cleanupHandler;
        private static List<Action> rs_cleanUpActions;

        static ApplicationExitHandler()
        {
            rs_cleanUpActions = new List<Action>();
            rs_cleanupHandler += CleanUp;
            SetConsoleCtrlHandler(rs_cleanupHandler, true);
        }

        private static bool CleanUp(CtrlType sig)
        {
            if (rs_cleanUpActions.Count > 0)
            {
                rs_log.Debug($"{sig} event detected: cleaning up...");
                foreach (var action in rs_cleanUpActions)
                {
                    try
                    {
                        action();
                    }
                    catch (Exception ex)
                    {
                        rs_log.Error("CantCleanup", ex);
                    }
                }
            }
            else
            {
                rs_log.Debug($"{sig} event detected.");
            }

            //shutdown right away so there are no lingering threads
            Environment.Exit(-1);

            return true;
        }

        public static void AddCleanUpAction(Action action)
        {
            rs_cleanUpActions.Add(action);
        }

        public static void RemoveCleanUpAction(Action[] actions = null)
        {
            if (actions == null)
            {
                rs_cleanUpActions.Clear();
            }
            else
            {
                foreach (var action in actions)
                {
                    rs_cleanUpActions.Remove(action);
                }
            }
        }

        public static void AddKillProcessAction(Process process)
        {
            AddCleanUpAction(() => {
                rs_log.Error($"Killing process {process.Id}, {process.ProcessName}");
                process.Kill();
            });
        }

        public static void AddKillProcessAction(string processName, TimeSpan startedSince)
        {
            foreach (var p in Process
                .GetProcesses()
                .Where(p => p.ProcessName.IndexOf(processName, StringComparison.OrdinalIgnoreCase) >= 0)
                .Where(p => DateTime.Now - p.StartTime < startedSince))
            {
                AddKillProcessAction(p);
            }
        }

        [DllImport("Kernel32")]
        private static extern bool SetConsoleCtrlHandler(EventHandler handler, bool add);

        enum CtrlType
        {
            CTRL_C_EVENT = 0,
            CTRL_BREAK_EVENT = 1,
            CTRL_CLOSE_EVENT = 2,
            CTRL_LOGOFF_EVENT = 5,
            CTRL_SHUTDOWN_EVENT = 6
        }
    }
}
