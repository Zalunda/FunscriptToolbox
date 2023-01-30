using FunscriptToolbox.Core;
using Mpv.NET.Player;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Imaging;

namespace FunscriptToolbox.UI
{
    /// <summary>
    /// Interaction logic for MotionVectorsEditor.xaml
    /// </summary>
    public partial class MotionVectorsEditor : Window
    {
        private readonly string r_videoFileName;
        private readonly string r_outputParametersFileName;
        private readonly Input r_input;
        private readonly MotionVectorsFileReader r_reader;
        private readonly MpvPlayer r_player;

        private System.Windows.Point m_temporaryStartPoint;
        private System.Windows.Shapes.Rectangle m_temporarySelection;
        private List<FunscriptAction> m_currentActions;

        private class Input
        {
            public double CurrentTime { get; set; }
            public double EndTime { get; set; }
            public FunscriptAction[] Actions { get; set; }
        };

        public MotionVectorsEditor(string videoFileName, string inputParametersFileName, string outputParametersFileName)
        {
            InitializeComponent();

            r_videoFileName = videoFileName;
            r_input = JsonConvert.DeserializeObject<Input>(File.ReadAllText(inputParametersFileName));
            r_outputParametersFileName = outputParametersFileName;

            // TODO: Use funscript name
            var mvsFile = Path.ChangeExtension(
                videoFileName.Replace(".mvs-visual", ""), ".mvs");
            r_reader = new MotionVectorsFileReader(
                mvsFile,
                1000);

            var applicationFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            r_player = new MpvPlayer(PlayerHost.Handle, Path.Combine(applicationFolder, "mpv-1.dll"));

            PlayerGrid.Width = r_reader.VideoWidth;
            PlayerGrid.Height = r_reader.VideoHeight;
            PlayerScreenShot.Width = r_reader.VideoWidth;
            PlayerScreenShot.Height = r_reader.VideoHeight;
        }

        private void WindowsOnLoaded(object sender, RoutedEventArgs e)
        {
            r_player.MediaLoaded += (s, arg) => { 
                r_player.Position = TimeSpan.FromMilliseconds(r_input.CurrentTime);
            };
            r_player.Load(r_videoFileName);
            r_player.Resume();
            r_player.Pause();
        }

        private void WindowOnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            r_player.Dispose();

            if (m_currentActions != null)
            {
                var content = JsonConvert.SerializeObject(new
                {
                    Actions = m_currentActions.ToArray()
                });
                File.WriteAllText(r_outputParametersFileName, content);
            }
        }

        private void playButton_Click(object sender, RoutedEventArgs e)
        {
            if (r_player.IsPlaying)
                FakePause();
            else
                r_player.Resume();
        }

        private void frameButton_Click(object sender, RoutedEventArgs e)
        {
            Player.Visibility = Visibility.Visible;
            PlayerKindOfOverlay.Visibility = Visibility.Collapsed;
            r_player.NextFrame();
        }

        private void PlayerHost_MouseDown(object sender, System.Windows.Forms.MouseEventArgs e)
        {
            FakePause();
        }

        private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
        {

            m_temporaryStartPoint = e.GetPosition(PlayerKindOfOverlay);
            m_temporarySelection = new System.Windows.Shapes.Rectangle
            {
                Stroke = System.Windows.Media.Brushes.Black,
                StrokeThickness = 2
            };
            Canvas.SetLeft(m_temporarySelection, m_temporaryStartPoint.X);
            Canvas.SetTop(m_temporarySelection, m_temporaryStartPoint.Y);

            PlayerKindOfOverlay.Children.Add(m_temporarySelection);
        }

        private void Canvas_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var pos = e.GetPosition(PlayerKindOfOverlay);
                var x = Math.Min(pos.X, m_temporaryStartPoint.X);
                var y = Math.Min(pos.Y, m_temporaryStartPoint.Y);
                var width = Math.Max(pos.X, m_temporaryStartPoint.X) - x;
                var height = Math.Max(pos.Y, m_temporaryStartPoint.Y) - y;

                m_temporarySelection.Width = width;
                m_temporarySelection.Height = height;

                Canvas.SetLeft(m_temporarySelection, x);
                Canvas.SetTop(m_temporarySelection, y);
            }
        }

        private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var pos = e.GetPosition(PlayerKindOfOverlay);
            var x = Math.Min(pos.X, m_temporaryStartPoint.X);
            var y = Math.Min(pos.Y, m_temporaryStartPoint.Y);
            var width = Math.Max(pos.X, m_temporaryStartPoint.X) - x;
            var height = Math.Max(pos.Y, m_temporaryStartPoint.Y) - y;

            CreateActions(x, y, width, height);
        }

        private void CreateActions(double x, double y, double width, double height)
        {
            var rules = new List<FrameAnalyserRule>();
            for (int indexBlocY = 0; indexBlocY < r_reader.NbBlocY; indexBlocY++)
            {
                var blocY = indexBlocY * r_reader.BlocSize;
                if (blocY >= y && blocY < y + height)
                {
                    for (int indexBlocX = 0; indexBlocX < r_reader.NbBlocX; indexBlocX++)
                    {
                        var blocX = indexBlocX * r_reader.BlocSize;
                        if (blocX >= x && blocX < x + width)
                        {
                            rules.Add(new FrameAnalyserRule(indexBlocY * r_reader.NbBlocX + indexBlocX, MotionVectorDirection.Up));
                        }
                    }
                }
            }

            var frameAnalyser = new FrameAnalyserGeneric(
                "SumMotionY",
                rules);
            foreach (var frame in r_reader.ReadFrames(r_player.Position, TimeSpan.FromMilliseconds(r_input.EndTime)))
            {
                frameAnalyser.AddFrameData(frame);
            }
            m_currentActions = frameAnalyser.Actions;
        }

        private void FakePause()
        {
            r_player.Pause();

            var size = new System.Drawing.Size(PlayerHost.Width, PlayerHost.Height);
            Bitmap bmpScreenshot = new Bitmap(size.Width, size.Height, PixelFormat.Format32bppArgb);

            // Create a Graphics object from the Bitmap
            Graphics gfxScreenshot = Graphics.FromImage(bmpScreenshot);

            var ptscreen = PlayerHost.PointToScreen(new System.Drawing.Point(0, 0));

            // Take a screenshot of the window and save it to the Bitmap
            gfxScreenshot.CopyFromScreen(ptscreen.X,
                                         ptscreen.Y,
                                         0, 0,
                                         size,
                                         CopyPixelOperation.SourceCopy);

            using (var memory = new MemoryStream())
            {
                bmpScreenshot.Save(memory, ImageFormat.Png);
                memory.Position = 0;
                var bitmapImage = new BitmapImage();
                bitmapImage.BeginInit();
                bitmapImage.StreamSource = memory;
                bitmapImage.CacheOption = BitmapCacheOption.OnLoad;
                bitmapImage.EndInit();
                bitmapImage.Freeze();

                PlayerScreenShot.Source = bitmapImage;
                Player.Visibility = Visibility.Collapsed;
                PlayerKindOfOverlay.Visibility = Visibility.Visible;
            }
        }
    }
}
