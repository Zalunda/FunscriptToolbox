using AudioSynchronization;
using CommandLine;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace FunscriptToolbox.AudioSyncVerbs
{
    class Program
    {
        static int HandleParseError(IEnumerable<Error> errs)
        {
            //handle errors
            return -1;
        }

        static int Main(string[] args)
        {
            try
            {
                return Parser.Default.ParseArguments<
                    VerbAudioSyncCreateAudioSignature.Options,
                    VerbAudioSyncCreateFunscript.Options>(args)
                    .MapResult(
                          (VerbAudioSyncCreateAudioSignature.Options options) => new VerbAudioSyncCreateAudioSignature(options).Execute(),
                          (VerbAudioSyncCreateFunscript.Options options) => new VerbAudioSyncCreateFunscript(options).Execute(),
                          errors => HandleParseError(errors));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine(ex.ToString());
                return -1;
            }

            //args = new[]
            //{
            //    @"C:\Partage\Medias\Adult\Videos VR\Rated-6\JAV - 3DSVR-0647 (SLR) - Kurea Hasumi - Strip Club - 20102 [zalunda].mp4",
            //    @"C:\Partage\Medias\Adult\Videos VR\Rated-6\JAV - 3DSVR-0647-B - Kurea Hasumi - Strip Club [zalunda].mp4",
            //};

            //try
            //{
            //    Console.Error.WriteLine("*******************************");
            //    List<AudioSignature> audioSignatureCollection = new List<AudioSignature>();
            //    foreach (var arg in args)
            //    {
            //        var debut = DateTime.Now;
            //        if (Path.GetExtension(arg) == ".asig")
            //        {
            //            var funscript = Funscript.FromFile(arg);
            //            audioSignatureCollection.Add(funscript.AudioSignature);
            //        }
            //        else
            //        {
            //            var analyzer = new AudioTracksAnalyzer();
            //            var funscript = new Funscript
            //            {
            //                AudioSignature = analyzer.ExtractSignature(arg)
            //            };

            //            audioSignatureCollection.Add(funscript.AudioSignature);
            //            funscript.Save(Path.ChangeExtension(arg, ".asig"));
            //        }
            //        Console.Error.WriteLine("File = '{0}', Time = {1}", arg, DateTime.Now - debut);
            //    }


            //    if (audioSignatureCollection.Count == 2)
            //    {
            //        var first = audioSignatureCollection[0];
            //        var second = audioSignatureCollection[1];

            //        SamplesComparer comparer = new SamplesComparer(
            //            first.GetUncompressedSamples().ToArray(), 
            //            second.GetUncompressedSamples().ToArray(), 
            //            new CompareOptions
            //            {
            //                MinimumMatchLength = (int)TimeSpan.FromSeconds(10).TotalSeconds * 120,
            //                NbPeaksPerMinute = 10
            //            });
            //        List<SamplesSectionDiff> matches = comparer.Compare();
            //        //AudioTracksShiftFile shiftFile = new AudioTracksShiftFile(first, second);
            //        //shiftFile.AddRange(matches);
            //        //shiftFile.Save(Path.Combine(
            //        //            Path.GetDirectoryName(args[0]),
            //        //            Path.GetFileNameWithoutExtension(first.OriginalFileName) + "-" + Path.GetFileNameWithoutExtension(second.OriginalFileName) + ".shift"));

            //        //TimeShiftViewer viewer = new TimeShiftViewer(shiftFile);
            //        //viewer.ShowDialog();
            //        //int asdaskdj = 0;
            //    }
            //}
            //catch (Exception ex)
            //{
            //    Console.WriteLine(ex.ToString());
            //}


            //var chemin = @"C:\Partage\Medias\Adult\Videos VR\NotRated\18VR-Veronica Leal - Dirty Weekend Gateaway.mp4";
            //chemin = @"C:\Partage\Medias\Adult\Videos\NotRated\Aisha Bunny - Japanese bunny girl shows the way to suck the dick properly.mp4";
            //var analyzer = new AudioTracksAnalyzer();
            //var funscript = new Funscript
            //{
            //    AudioSignature = analyzer.ExtractSignature(chemin)
            //};
            //var t = funscript.AudioSignature.GetUncompressedSamples().ToArray();



            //funscript.Save("test.asig");
        }
    }
}
