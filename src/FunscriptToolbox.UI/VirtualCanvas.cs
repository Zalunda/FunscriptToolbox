using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace FunscriptToolbox.UI
{
    public class VirtualCanvas : Canvas
    {
        private ScaleTransform scaleTransform;
        private TranslateTransform translateTransform;
        private TransformGroup transformGroup;
        private Point lastPosition;

        public VirtualCanvas()
        {
            this.scaleTransform = new ScaleTransform();
            this.translateTransform = new TranslateTransform();
            this.transformGroup = new TransformGroup();
            this.transformGroup.Children.Add(scaleTransform);
            this.transformGroup.Children.Add(translateTransform);

            this.RenderTransform = transformGroup;

            this.MouseWheel += OnMouseWheel;
            this.MouseLeftButtonDown += OnMouseLeftButtonDown;
            this.MouseLeftButtonUp += OnMouseLeftButtonUp;
            this.MouseMove += OnMouseMove;
        }

        private void OnMouseWheel(object sender, MouseWheelEventArgs e)
        {
            double zoom = e.Delta > 0 ? 1.1 : 0.9;
            scaleTransform.ScaleX *= zoom;
            scaleTransform.ScaleY *= zoom;
        }

        private void OnMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            this.Cursor = Cursors.Hand;
            this.CaptureMouse();
        }

        private void OnMouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            this.Cursor = Cursors.Arrow;
            this.ReleaseMouseCapture();
        }

        private void OnMouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                Point currentPosition = e.GetPosition(this);
                translateTransform.X += currentPosition.X - lastPosition.X;
                translateTransform.Y += currentPosition.Y - lastPosition.Y;
                lastPosition = currentPosition;
            }
        }
    }
}
