using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core
{
    public class WIPFunscriptAction
    {
        internal static WIPFunscriptActionDirection GetDirection(int? currentPos, int? prevPos)
        {
            return (currentPos == null || prevPos == null)
                ? WIPFunscriptActionDirection.Undefined
                : currentPos > prevPos
                ? WIPFunscriptActionDirection.Up
                : currentPos < prevPos
                ? WIPFunscriptActionDirection.Down
                : WIPFunscriptActionDirection.Flat;
        }

        private const int MAX_ERROR = 300*300*300;
        static WIPFunscriptAction()
        {
            rs_errorScale = new int[200];
            for (int i = 0; i < rs_errorScale.Length; i++)
            {                
                if (i < 3)
                {
                    rs_errorScale[i] = i * i; // 0, 1, 4
                }
                else
                {
                    rs_errorScale[i] = i * i * i; // 27, 81, 4
                }
            }
        }

        private static readonly int[] rs_errorScale;

        public WIPFunscriptAction(
            IEnumerable<FunscriptAction> originalActions,
            WIPFunscriptAction previousAction)
        {
            this.OriginalActions = originalActions.ToArray();

            this.PreviousAction = previousAction;
            this.At = this.OriginalActions.Last().At;
            this.Duration = previousAction == null ? 0 : this.At - previousAction.At;
            this.OriginalPos = this.OriginalActions.Last().Pos;
            this.OriginalDistance = previousAction == null ? 0 : this.OriginalPos - previousAction.OriginalPos;
            this.OriginalSpeed = ComputeSpeed(this.OriginalDistance, this.Duration);
            this.Direction = GetDirection(this.OriginalPos, previousAction?.OriginalPos);

            this.WantedDistance = this.OriginalDistance;
            this.MinSpeed = null;
            this.MaxSpeed = null;

            this.NewPos = this.OriginalPos;
        }

        private static double ComputeSpeed(int originalDistance, int originalDuration)
        {
            return (originalDuration == 0)
                ? 0
                : (double)1000 * originalDistance / originalDuration;
        }

        // Set by constructor
        public FunscriptAction[] OriginalActions { get; }
        public WIPFunscriptAction PreviousAction { get; }
        public int At { get; }
        public int Duration { get; }
        public WIPFunscriptActionDirection Direction { get; }
        public int OriginalPos { get; }
        public int OriginalDistance { get; }
        public double OriginalSpeed { get; }

        // Set by SetNextAction()
        public WIPFunscriptAction NextAction { get; private set; }

        // Set by SetDistance, SetMinSpeed, SetMaxSpeed
        public double WantedDistance { get; private set; }
        public double? MinSpeed { get; private set; }
        public double? MaxSpeed { get; private set; }

        // Changed Nudge()
        public int NewPos { get; private set; }

        // Computed dynamically
        public int ComputedNewDistance => NewPos - PreviousAction?.NewPos ?? 0;
        public double ComputedNewSpeed => ComputeSpeed(this.ComputedNewDistance, this.Duration);
        public int ComputedPositionError => Math.Abs(OriginalPos - NewPos);
        public double ComputedDistanceError => Math.Abs(WantedDistance - ComputedNewDistance);
        public double ComputedSpeedError => Math.Abs(OriginalSpeed - ComputedNewSpeed);

        public double ComputedError
        {
            get
            {
                var totalError = 0.0;

                var newSpeed = this.ComputedNewSpeed;
                // DISABLED MinSpeed check for now. It's not working yet.
                //if (this.MinSpeed != null && newSpeed != 0 && Math.Abs(newSpeed) < this.MinSpeed)
                //{
                //    totalError += (Math.Abs(this.MinSpeed.Value) - newSpeed) * MAX_ERROR + rs_errorScale[100 - (int)ComputedDistanceError];
                //}
                if (this.MaxSpeed != null && Math.Abs(newSpeed) > this.MaxSpeed)
                {
                    totalError += (Math.Abs(newSpeed) - this.MaxSpeed.Value) * MAX_ERROR + rs_errorScale[(int)ComputedDistanceError];
                }

                // If scale increase or don't change, always compute both error.
                // If scale decrease, only considered the error for the point nearest to the border (0 or 100) of the center of the orinal wave.
                if ((Math.Abs(OriginalSpeed) <= Math.Abs(ComputedNewSpeed)) || (Direction == WIPFunscriptActionDirection.Up && ((PreviousAction.OriginalPos + OriginalPos) / 2) > 50))
                {
                    totalError += rs_errorScale[ComputedPositionError];
                }
                if ((Math.Abs(OriginalSpeed) <= Math.Abs(ComputedNewSpeed)) || (Direction == WIPFunscriptActionDirection.Down && ((PreviousAction.OriginalPos + OriginalPos) / 2) < 50))
                {
                    totalError += rs_errorScale[ComputedPositionError];
                }

                return totalError + rs_errorScale[(int)ComputedDistanceError + 7] + ComputedDistanceError;
            }
        }

        internal void SetNextAction(WIPFunscriptAction nextAction)
        {
            this.NextAction = nextAction;
        }

        internal void SetWantedDistance(double wantedDistance)
        {
            this.WantedDistance = Math.Max(-100.0, Math.Min(100.0, wantedDistance));
        }

        internal void SetMinSpeed(double minSpeed) 
        { 
            this.MinSpeed = minSpeed;
        }
        internal void SetMaxSpeed(double maxSpeed)
        {
            this.MaxSpeed = maxSpeed;
        }

        public bool Nudge()
        {
            double CalculateLocalError()
            {
                double totalError = ComputedError;
                if (PreviousAction != null)
                {
                    totalError += PreviousAction.ComputedError;
                }
                if (PreviousAction?.PreviousAction != null)
                {
                    totalError += PreviousAction.PreviousAction.ComputedError;
                }
                if (NextAction != null)
                {
                    totalError += NextAction.ComputedError;
                }
                if (NextAction?.NextAction != null)
                {
                    totalError += NextAction.NextAction.ComputedError;
                }
                return totalError;
            }
            
            double currentError = CalculateLocalError();

            // Try moving up (both point first, then only the current point)
            if (NewPos < 100 && PreviousAction?.NewPos < 100)
            {
                NewPos++;
                PreviousAction.NewPos++;
                double upError = CalculateLocalError();
                if (upError < currentError)
                {
                    return true; // Improvement found
                }
                NewPos--; // Revert if no improvement
                PreviousAction.NewPos--;
            }

            if (NewPos < 100)
            {
                NewPos++;
                double upError = CalculateLocalError();
                if (upError < currentError)
                {
                    return true; // Improvement found
                }
                NewPos--; // Revert if no improvement
            }

            // Try moving down  (both point first, then only the current point)
            if (NewPos > 0 && PreviousAction?.NewPos > 0)
            {
                NewPos--;
                PreviousAction.NewPos--;
                double downError = CalculateLocalError();
                if (downError < currentError)
                {
                    return true; // Improvement found
                }
                NewPos++; // Revert if no improvement
                PreviousAction.NewPos++;
            }

            if (NewPos > 0)
            {
                NewPos--;
                double downError = CalculateLocalError();
                if (downError < currentError)
                {
                    return true; // Improvement found
                }
                NewPos++; // Revert if no improvement
            }

            return false; // No improvement found
        }

        public override string ToString()
        {
            return $"ERROR: {this.ComputedError}, OriginalPos: {this.OriginalPos}, OriginalDistance: {this.OriginalDistance}, NewPos: {this.NewPos}, ExpectedDistance: {this.WantedDistance}, NewDistance: {this.ComputedNewDistance}, Error: P:{this.ComputedPositionError}, d:{this.ComputedDistanceError}";
        }
    }
}
