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

        public WIPFunscriptAction(
            IEnumerable<FunscriptAction> originalActions,
            WIPFunscriptAction previousAction)
        {
            this.OriginalActions = originalActions.ToArray();

            this.PreviousAction = previousAction;
            this.At = this.OriginalActions.Last().At;
            this.OriginalPos = this.OriginalActions.Last().Pos;
            this.OriginalDistance = previousAction == null ? 0 : this.OriginalPos - previousAction.OriginalPos;
            this.Direction = GetDirection(this.OriginalPos, previousAction?.OriginalPos);
            this.NewPos = this.OriginalPos;
        }

        // Set by the constructors
        public FunscriptAction[] OriginalActions { get; }
        public WIPFunscriptAction PreviousAction { get; }
        public int At { get; }
        public int OriginalPos { get; }
        public int OriginalDistance { get; }
        public WIPFunscriptActionDirection Direction { get; }

        // Set by the SetNextAction()
        public WIPFunscriptAction NextAction { get; private set; }

        // Set by the Scale()
        public int ExpectedDistance { get; private set; }

        // Changed by Nudge()
        public int NewPos { get; private set; }

        // Computed dynamically
        public int ComputedNewDistance => NewPos - PreviousAction?.NewPos ?? 0;
        public int ComputedPositionError => Math.Abs(OriginalPos - NewPos);
        public int ComputedDistanceError => Math.Abs(ExpectedDistance - ComputedNewDistance);

        public double ComputedError
        {
            get
            {
                double NonLinearFunctionPosition(int error)
                {
                    // Only considerer the error for the point nearest to the border (0 or 100) of the center of the orinal wave.
                    if ((Direction == WIPFunscriptActionDirection.Up && ((OriginalPos + PreviousAction.OriginalPos) / 2) > 50)
                        || (Direction == WIPFunscriptActionDirection.Down && ((PreviousAction.OriginalPos + OriginalPos) / 2) < 50))
                    {
                        return Math.Pow(error, 1.2);
                    }
                    else
                    {
                        return 0;
                    }
                }
                double NonLinearFunctionDistance(int error)
                {
                    return Math.Pow(error, 5);
                }

                return NonLinearFunctionPosition(ComputedPositionError) + 10 * NonLinearFunctionDistance(ComputedDistanceError);
            }
        }

        internal void SetNextAction(WIPFunscriptAction nextAction)
        {
            this.NextAction = nextAction;
        }

        internal void Scale(double scale, double add = 0.0)
        {
            this.ExpectedDistance = Math.Min(100, (int)Math.Round(this.OriginalDistance * scale + add));
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

            // Try moving up
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

            // Try moving down
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
            return $"ERROR: {this.ComputedError}, OriginalPos: {this.OriginalPos}, OriginalDistance: {this.OriginalDistance}, NewPos: {this.NewPos}, ExpectedDistance: {this.ExpectedDistance}, NewDistance: {this.ComputedNewDistance}, Error: P:{this.ComputedPositionError}, d:{this.ComputedDistanceError}";
        }
    }

}
