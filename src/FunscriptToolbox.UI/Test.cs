using FunscriptToolbox.UI.SpeakerCorrection;
using System;
using System.Collections.Generic;
using System.Threading;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
        public static bool SpeakerCorrection(
            IEnumerable<SpeakerCorrectionWorkItem> workItems,
            Func<TimeSpan, (string fullpath, TimeSpan newPosition)> getPathAndPositionFunc,
            Action<SpeakerCorrectionWorkItem> saveCallBack,
            Action<SpeakerCorrectionWorkItem> undoCallBack)
        {
            SpeakerCorrectionTool tool = null;
            Exception threadEx = null;
            var thread = new Thread(() => {
                try
                {
                    tool = new SpeakerCorrectionTool(
                        workItems,
                        getPathAndPositionFunc,
                        saveCallBack,
                        undoCallBack);
                    tool.ShowDialog();
                }
                catch (Exception ex)
                {
                    threadEx = ex;
                }
            });
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            thread.Join();
            if (threadEx != null)
                throw new Exception("Rethrown exception.", threadEx);

            return tool.IsFinishForced;
        }
    }
}
