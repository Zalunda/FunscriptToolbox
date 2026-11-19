﻿using FunscriptToolbox.Progress;
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
            bool isVerbose,
            string prefix,
            IUserOutput output)
        {
            r_log = log;
            this.IsVerbose = isVerbose;
            this.Prefix = prefix;
            this.Output = output;
            this.Errors = new List<string>();
        }

        private readonly ILog r_log;

        /// <summary>Where everything shown to the user goes (console, or web page in --ui mode).</summary>
        public IUserOutput Output { get; }

        public string Prefix { get; private set; }
        public bool IsVerbose { get; }
        public List<string> Errors { get; }

        public void ChangePrefix(string prefix)
        {
            this.Prefix = prefix;
        }

        // In the web UI, the task tree already tells which video the message is about: no prefix needed.
        private string ForDisplay(string message) => this.Output.IsWebUi ? message : this.Prefix + message;

        public void WriteInfo(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            r_log.Info(this.Prefix + message);
            this.Output.WriteInfo(ForDisplay(message), color, isProgress);
        }

        public void WriteVerbose(string message = "") => WriteVerbose(message, null, false);

        public void WriteVerbose(string message = "", ConsoleColor? color = null, bool isProgress = false)
        {
            r_log.Debug(this.Prefix + message);
            this.Output.WriteVerbose(ForDisplay(message), color, isProgress, this.IsVerbose);
        }

        public void WriteDebug(string message = "")
        {
            message = this.Prefix + message;

            r_log.Debug(message);
        }

        public void WriteError(string message = "")
        {
            r_log.Error(this.Prefix + message);
            this.Output.WriteError(ForDisplay(message));
            lock (this.Errors)
            {
                this.Errors.Add(this.Prefix + message);
            }
        }

        internal void WriteLog(string message = "")
        {
            message = this.Prefix + message;
            r_log.Info(message);
        }

        protected static string FormatTimeSpan(TimeSpan value)
        {
            return Regex.Replace(value.ToString(), @"\d{4}$", "");
        }
    }
}
