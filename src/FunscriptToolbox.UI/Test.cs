using FunscriptToolbox.Core.MotionVectors;
using FunscriptToolbox.Core.MotionVectors.PluginMessages;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
        [STAThread]
        public static FrameAnalyser TestAnalyser(
            Task<byte[]> snapshotContent, 
            MotionVectorsFileReader mvsReader, 
            CreateRulesPluginRequest createRulesFromScriptActions)
        {
            MotionVectorsEditor editor = null;
            Exception threadEx = null;
            var thread = new Thread(() => {
                try
                {
                    editor = new MotionVectorsEditor(
                        snapshotContent, 
                        mvsReader, 
                        createRulesFromScriptActions);
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

            return editor?.FinalFrameAnalyser;
        }
    }
}
