using log4net;
using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace FunscriptToolbox
{
    public class VerbContext
    {
        public VerbContext(
            ILog log,
            string prefix,
            bool isVerbose)
        {
            r_log = log;
            this.Prefix = prefix;
            this.IsVerbose = isVerbose;
            this.Errors = new List<string>();
        }

        private readonly ILog r_log;
        private bool m_lastWriteIsProgress = false;

        public string Prefix { get; }
        public bool IsVerbose { get; }
        public List<string> Errors { get; }

        public void WriteInfo(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            message = this.Prefix + message;

            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Info(message);
            if (color != null) Console.ForegroundColor = color.Value;
            if (isProgress)
            {
                var padding = Console.WindowWidth - message.Length - 1;
                Console.Write(
                    (padding < 0)
                    ? $"{message.Substring(0, Console.WindowWidth - 1)}\r"
                    : $"{message}{new string(' ', padding)}\r");
            }
            else
            {
                Console.WriteLine($"{message}");
            }
            Console.ResetColor();
            m_lastWriteIsProgress = isProgress;
        }

        public void WriteVerbose(string message = "") => WriteVerbose(message, null, false);

        public void WriteVerbose(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            message = this.Prefix + message;

            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Debug(message);
            if (this.IsVerbose)
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
            message = this.Prefix + message;

            r_log.Debug(message);
        }

        public void WriteError(string message = "")
        {
            message = this.Prefix + message;

            if (m_lastWriteIsProgress)
            {
                Console.Write($"{new string(' ', Math.Max(0, Console.WindowWidth - 1))}\r");
            }

            r_log.Error(message);
            Console.ForegroundColor = ConsoleColor.Red;
            Console.Error.WriteLine(message);
            Console.ResetColor();
            this.Errors.Add(message);
        }

        protected static string FormatTimeSpan(TimeSpan value)
        {
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
        }
    }
}