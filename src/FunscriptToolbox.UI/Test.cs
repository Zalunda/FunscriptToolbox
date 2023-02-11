using FunscriptToolbox.Core.MotionVectors;
using System;
using System.Threading;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
        [STAThread]
        public static FrameAnalyser TestAnalyser(byte[] snapshotContent, TimeSpan videoTime, MotionVectorsFileReader mvsReader, FrameAnalyser frameAnalyser)
        {
            Exception threadEx = null;
            var thread = new Thread(() => {
                try
                {
                    var editor = new MotionVectorsEditor(snapshotContent, videoTime, mvsReader, frameAnalyser);
                    editor.ShowDialog();
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

            return frameAnalyser; // TODO
        }
    }
}
