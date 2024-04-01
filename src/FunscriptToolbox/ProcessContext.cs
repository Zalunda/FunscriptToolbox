using log4net;
using System;
using System.Text.RegularExpressions;

namespace FunscriptToolbox
{
    public class ProcessContext
    {
        public ProcessContext(
            ILog log,
            bool isVerbose,
            string baseFilePath)
        {
            r_log = log;
            r_isVerbose = isVerbose;
            this.BaseFilePath = baseFilePath;
        }

        private readonly ILog r_log;
        private readonly bool r_isVerbose;
        public string BaseFilePath { get; }

        private bool m_lastWriteIsProgress = false;
        public int NbErrors { get; set; } = 0;

        public void WriteInfo(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Info(message);
            if (color != null) Console.ForegroundColor = color.Value;
            if (isProgress)
            {
                Console.Write($"{message}{new string(' ', Math.Max(0, Console.WindowWidth - message.Length - 1))}\r");
            }
            else
            {
                Console.WriteLine(message);
            }
            Console.ResetColor();
            m_lastWriteIsProgress = isProgress;
        }

        public void WriteVerbose(string message = "") => WriteVerbose(message, null, false);

        public void WriteVerbose(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Debug(message);
            if (r_isVerbose)
            {
                if (color != null) Console.ForegroundColor = color.Value;
                if (isProgress)
                {
                    Console.Write($"{message}{new string(' ', Math.Max(0, Console.WindowWidth - message.Length - 1))}\r");
                }
                else
                {
                    Console.WriteLine(message);
                }
                Console.ResetColor();
            }
            m_lastWriteIsProgress = isProgress;
        }

        public void WriteDebug(string message = "")
        {
            r_log.Debug(message);
        }

        public void WriteError(string message = "")
        {
            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Error(message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            Console.ResetColor();
            this.NbErrors++;
        }

        protected static string FormatTimeSpan(TimeSpan value)
        {
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
        }
    }
}