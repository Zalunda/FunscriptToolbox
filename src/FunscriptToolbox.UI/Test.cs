using FunscriptToolbox.Core.MotionVectors;
using FunscriptToolbox.Core.MotionVectors.PluginMessages;
using FunscriptToolbox.UI.SpeakerCorrection;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace FunscriptToolbox.UI
{
    public static class Test
    {
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

        public static SpeakerCorrectionWorkItem[] SpeakerCorrection(
            string videopath,
            IEnumerable<SpeakerCorrectionWorkItem> workItems,
            Action<SpeakerCorrectionWorkItem> saveCallBack,
            Action<SpeakerCorrectionWorkItem> undoCallBack)
        {
            SpeakerCorrectionTool tool = null;
            Exception threadEx = null;
            var thread = new Thread(() => {
                try
                {
                    tool = new SpeakerCorrectionTool(
                        videopath,
                        workItems,
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

            return tool?.WorkItems.ToArray();
        }
    }
}
