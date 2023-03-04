using System;

namespace FunscriptToolbox.Core.MotionVectors.PluginMessages
{
    public class ErrorPluginResponse : PluginResponse
    {
        public string ErrorMessage { get; }

        public ErrorPluginResponse(Exception ex)
        {
            this.ErrorMessage = ex.ToString();
        }
    }
}

