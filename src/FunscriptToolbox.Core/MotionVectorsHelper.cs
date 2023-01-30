using System;

namespace FunscriptToolbox.Core
{
    public static class MotionVectorsHelper
    {
        public const int NbBaseAngles = 16;
        static private short[,,] rs_lLookupMotionXMotionYAngle;
        static private object rs_lLookupMotionXMotionYAngleLock = new object();
        static private short[,,] LookupMotionXMotionYAngle
        {
            get
            {
                if (rs_lLookupMotionXMotionYAngle == null)
                {
                    lock (rs_lLookupMotionXMotionYAngleLock)
                    {
                        if (rs_lLookupMotionXMotionYAngle == null)
                        {
                            rs_lLookupMotionXMotionYAngle = new short[byte.MaxValue + 1, byte.MaxValue + 1, NbBaseAngles];
                            for (int xAsByte = 0; xAsByte <= byte.MaxValue; xAsByte++)
                            {
                                var x = (sbyte)xAsByte;
                                for (int yAsByte = 0; yAsByte <= byte.MaxValue; yAsByte++)
                                {
                                    var y = (sbyte)yAsByte;
                                    double length = Math.Sqrt(x * x + y * y);
                                    double angle = Math.Atan2(y, x);

                                    for (int i = 0; i < NbBaseAngles; i++)
                                    {
                                        var baseAngle = (i + 4) * (2 * Math.PI / NbBaseAngles); // i + 4 is because we want to have the value 0 represente Up (like a clock)
                                        double diff = angle - baseAngle;
                                        rs_lLookupMotionXMotionYAngle[xAsByte, yAsByte, i] = (short)Math.Round(10 * length * Math.Cos(diff));
                                    }
                                }
                            }
                        }
                    }
                }
                return rs_lLookupMotionXMotionYAngle;
            }
        }

        /// <summary>
        /// Lookup table. Given motionX as a byte (even if the value is handled as sbyte), motionY as a byte and the angle to compare it too.
        /// For example: 
        ///     FrameAnalyser.LookupMotionXMotionYAngle[(byte)motionX][(byte)motionY][BaseAngles.Right] would return the length motion compared to right (i.e. 3h on an analog clock)
        ///     FrameAnalyser.LookupMotionXMotionYAngle[12][-3][BaseAngles.Right] => 120 
        /// Note: the length is multiplied by 100 to have more precision. GetFinalMotionLength(-10,0,Up) => Returns 1000 instead of 10.
        /// </summary>
        public static short GetFinalMotionLength(byte motionXAsByte, byte motionYAsByte, MotionVectorDirection angle)
        {
            return LookupMotionXMotionYAngle[motionXAsByte, motionYAsByte, (int)angle];
        }

        //var elapsedA = stopwatch.Elapsed;
        //Console.WriteLine($"FINAL: {elapsedA}");

        //var temp = new short[reader.NbBlocTotalPerFrame, nbBaseAngles, 3];
        //var indexAction = 0;
        //var nextAction = r_funscriptOriginal.Actions[indexAction++];
        //var currentAction = new FunscriptAction { At = 0, Pos = nextAction.Pos };
        //foreach (var frame in reader.ReadFrames())
        //{
        //    while (frame.FrameTimeInMs.TotalMilliseconds >= nextAction.At)
        //    {
        //        currentAction = nextAction;
        //        nextAction = (indexAction < r_funscriptOriginal.Actions.Length)
        //            ? r_funscriptOriginal.Actions[indexAction++]
        //            : new FunscriptAction { At = (int)reader.VideoDuration.TotalMilliseconds, Pos = currentAction.Pos };
        //    }

        //    var currentUpDownMotionInActionInMs = (double)(nextAction.Pos - currentAction.Pos) / (nextAction.At - currentAction.At);
        //    var currentUpDownMotionInFrame = currentUpDownMotionInActionInMs * (1000 / (double)reader.VideoFramerate);
        //    var posOrNegInFunscript = (currentUpDownMotionInFrame > 0)
        //        ? 1
        //        : (currentUpDownMotionInFrame < 0)
        //        ? -1
        //        : 0;

        //    for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
        //    {
        //        var motionXAsByte = frame.MotionsX[i];
        //        var motionYAsByte = frame.MotionsY[i];
        //        for (int k = 0; k < nbBaseAngles; k++)
        //        {
        //            var motionLength = lookup[motionXAsByte, motionYAsByte, k];
        //            var posOrNeg = (motionLength > 0)
        //                ? 1
        //                : (motionLength < 0)
        //                ? -1
        //                : 0;

        //            if (posOrNegInFunscript == posOrNeg)
        //            {
        //                temp[i, k, 0]++;
        //            }
        //            else if (posOrNeg == 0)
        //            {
        //                temp[i, k, 1]++;
        //            }
        //            else
        //            {
        //                temp[i, k, 2]++;
        //            }
        //        }
        //    }
        //}

        //var bestsAngle = new int[reader.NbBlocTotalPerFrame];
        //var bestsValue = new int[reader.NbBlocTotalPerFrame, 3];
        //for (int i = 0; i < reader.NbBlocTotalPerFrame; i++)
        //{
        //    var bestMatchValue = new[] { int.MinValue, 0, 0 };
        //    var bestMatchAngle = -1;
        //    for (int j = 0; j < nbBaseAngles; j++)
        //    {
        //        if (temp[i, j, 0] > bestMatchValue[0])
        //        {
        //            bestMatchValue[0] = temp[i, j, 0];
        //            bestMatchValue[1] = temp[i, j, 1];
        //            bestMatchValue[2] = temp[i, j, 2];
        //            bestMatchAngle = j;
        //        }
        //    }

        //    bestsAngle[i] = bestMatchAngle;
        //    bestsValue[i, 0] = bestMatchValue[0];
        //    bestsValue[i, 1] = bestMatchValue[1];
        //    bestsValue[i, 2] = bestMatchValue[2];
        //}

        //using (var writer = File.CreateText(Path.ChangeExtension(filename, ".bestmatch.log")))
        //{
        //    for (int percentage = 100; percentage >= 0; percentage -= 5)
        //    {
        //        var cutoff = reader.NbFrames * percentage / 100;
        //        if (true || 1000 >= cutoff)
        //        {
        //            writer.WriteLine($"----- {percentage:D3} ----------------------------------------------------------------------");

        //            for (int y = 0; y < reader.NbBlocY; y++)
        //            {
        //                for (int x = 0; x < reader.NbBlocX; x++)
        //                {
        //                    if (bestsValue[y * reader.NbBlocX + x, 0] + (bestsValue[y * reader.NbBlocX + x, 1] / 2) > cutoff)
        //                        writer.Write($"{bestsAngle[y * reader.NbBlocX + x]:X}");
        //                    else
        //                        writer.Write(" ");
        //                }
        //                writer.WriteLine();
        //            }
        //            writer.WriteLine();
        //            writer.WriteLine();
        //        }
        //    }

        //    for (int y = 0; y < reader.NbBlocY; y++)
        //    {
        //        for (int x = 0; x < reader.NbBlocX; x++)
        //        {
        //            writer.Write($"{bestsAngle[y * reader.NbBlocX + x]:X} {bestsValue[y * reader.NbBlocX + x, 0]:D5} {bestsValue[y * reader.NbBlocX + x, 1]:D5} {bestsValue[y * reader.NbBlocX + x, 2]:D5}-");
        //        }
        //        writer.WriteLine();
        //    }
        //    writer.WriteLine();
        //    writer.WriteLine();
        //}

        ////foreach (var analyser in frameAnalysers)
        ////{
        ////    analyser.AddFrameData(frame);
        ////}

        //stopwatch.Stop();
        //Console.WriteLine($"FINAL: {stopwatch.Elapsed}");

        ////foreach (var analyser in frameAnalysers)
        ////{
        ////    var funscriptFullpath = Path.ChangeExtension(filename, $".mvs-visual.{analyser.Name}.funscript");
        ////    var funscript = new Funscript();
        ////    funscript.Actions = analyser.Actions.ToArray();
        ////    funscript.Save(funscriptFullpath);
        ////}

    }
}
