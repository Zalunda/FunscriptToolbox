using FunscriptToolbox.Core.MotionVectors;
using FunscriptToolbox.Core.MotionVectors.PluginMessages;
using System;
using System.Threading;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
        [STAThread]
        public static FrameAnalyser TestAnalyser(
            byte[] snapshotContent, 
            MotionVectorsFileReader mvsReader, 
            CreateRulesFromScriptActionsPluginRequest createRulesFromScriptActions)
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
