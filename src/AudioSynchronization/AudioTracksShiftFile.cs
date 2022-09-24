using System;
using System.Collections.Generic;
using System.Text;
using System.Xml;

namespace AudioSynchronization
{
    //public class AudioTracksShiftFile : List<PeakListSectionDiff>
    //{
    //    public AudioTracksShiftFile()
    //    {
    //    }

    //    public AudioTracksShiftFile(PeaksFile peaksFileA, PeaksFile peaksFileB)
    //    {
    //        Format = 1;
    //        OriginalFileNameA = peaksFileA.OriginalFileName;
    //        OriginalFileLengthA = peaksFileA.OriginalFileLength;
    //        OriginalFileMD5A = peaksFileA.OriginalFileMD5;
    //        OriginalFileNameB = peaksFileB.OriginalFileName;
    //        OriginalFileLengthB = peaksFileB.OriginalFileLength;
    //        OriginalFileMD5B = peaksFileB.OriginalFileMD5;
    //        NbPeaksPerSeconds = 100;
    //    }

    //    public int Format { get; set; }
    //    public string OriginalFileNameA { get; set; }
    //    public long OriginalFileLengthA { get; set; }
    //    public string OriginalFileMD5A { get; set; }
    //    public string OriginalFileNameB { get; set; }
    //    public long OriginalFileLengthB { get; set; }
    //    public string OriginalFileMD5B { get; set; }
    //    public int NbPeaksPerSeconds { get; set; }

    //    public void Save(string filename)
    //    {
    //        XmlWriterSettings settings = new XmlWriterSettings
    //        {
    //            Encoding = Encoding.UTF8,
    //            CheckCharacters = false,
    //            NewLineOnAttributes = true,
    //            Indent = true
    //        };

    //        using (XmlWriter writer = XmlWriter.Create(filename, settings))
    //        {
    //            writer.WriteStartElement("AudioTracksShiftFile");
    //            writer.WriteAttributeString("format", Format.ToString());
    //            writer.WriteAttributeString("originalFileNameA", OriginalFileNameA);
    //            writer.WriteAttributeString("originalFileLengthA", OriginalFileLengthA.ToString());
    //            writer.WriteAttributeString("originalFileMD5A", OriginalFileMD5A);
    //            writer.WriteAttributeString("originalFileNameB", OriginalFileNameB);
    //            writer.WriteAttributeString("originalFileLengthB", OriginalFileLengthB.ToString());
    //            writer.WriteAttributeString("originalFileMD5B", OriginalFileMD5B);
    //            foreach (PeakListSectionDiff match in this)
    //            {
    //                writer.WriteStartElement("TimeShift");
    //                writer.WriteAttributeString("StartIndexA", TimeSpan.FromMilliseconds(match.SectionA.StartIndex * 10).ToString());
    //                writer.WriteAttributeString("StartIndexB", TimeSpan.FromMilliseconds(match.SectionB.StartIndex * 10).ToString());
    //                writer.WriteAttributeString("EndIndexA", TimeSpan.FromMilliseconds((match.SectionA.StartIndex + match.SectionA.Length) * 10 + 9).ToString());
    //                writer.WriteAttributeString("EndIndexB", TimeSpan.FromMilliseconds((match.SectionB.StartIndex + match.SectionB.Length) * 10 + 9).ToString());
    //                writer.WriteAttributeString("Length", TimeSpan.FromMilliseconds(match.SectionA.Length).ToString());
    //                writer.WriteAttributeString("AverageError", (match.TotalError / match.SectionA.Length).ToString("0.00%"));
    //                writer.WriteEndElement();
    //            }
    //            writer.WriteEndElement();
    //        }
    //    }
    //}
}
