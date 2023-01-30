using System;
using System.Threading;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
        [STAThread]
        public static void TestUI(string videoFileName, string inputParametersFileName, string outputParametersFileName)
        {
            Exception threadEx = null;
            var thread = new Thread(() => {
                try
                {
                    var editor = new MotionVectorsEditor(videoFileName, inputParametersFileName, outputParametersFileName);
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
        }
    }
}
