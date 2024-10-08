using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace FunscriptToolbox.Core
{
    // Example:
    // var funscript = Funscript.FromFile(filename);
    // var wipActions = WIPFunscriptActionCollection.FromFunscriptActions(funscript.Actions);
    // wipActions.Scale(scale); // scale : 0.5, 0.8, 1.2, etc
    // funscript.Actions = wipActions.ToFunscriptActions();
    // funscript.Save($"Test.{scale}.funscript");

    public class WIPFunscriptActionCollection : ReadOnlyCollection<WIPFunscriptAction>
    {
        public static WIPFunscriptActionCollection FromFunscriptActions(FunscriptAction[] actions)
        {
            if (actions == null || actions.Length == 0)
                return new WIPFunscriptActionCollection(Array.Empty<WIPFunscriptAction>());

            var tempWIPs = new List<List<FunscriptAction>>();
            var tempWIP = new List<FunscriptAction>
                {
                    actions[0]
                };
            tempWIPs.Add(tempWIP);

            var prevDirection = WIPFunscriptActionDirection.Undefined;

            for (int i = 1; i < actions.Length; i++)
            {
                var prevAction = actions[i - 1];
                var currentAction = actions[i];

                WIPFunscriptActionDirection currentDirection = WIPFunscriptAction.GetDirection(currentAction.Pos, prevAction.Pos);
                if (currentDirection == WIPFunscriptActionDirection.Flat || currentDirection == prevDirection)
                {
                    tempWIP.Add(currentAction);
                }
                else
                {
                    tempWIP = new List<FunscriptAction>
                    {
                        currentAction
                    };
                    tempWIPs.Add(tempWIP);
                    prevDirection = currentDirection;
                }
            }

            WIPFunscriptAction prevWIP = null;
            return new WIPFunscriptActionCollection(tempWIPs.Select(temp =>
            {
                var newWIP = new WIPFunscriptAction(temp, prevWIP);
                prevWIP?.SetNextAction(newWIP);
                prevWIP = newWIP;
                return newWIP;
            }));
        }

        public WIPFunscriptActionCollection(IEnumerable<WIPFunscriptAction> actions)
            : base(actions.ToArray())
        {
        }

        public void Scale(double scale, double add = 0.0)
        {
            foreach (var action in this)
            {
                action.SetWantedDistance(action.OriginalDistance * scale + add);
            }
        }

        public void SetMinSpeed(double minSpeed)
        {
            foreach (var action in this)
            {
                action.SetMinSpeed(minSpeed);
            }
        }

        public void SetMaxSpeed(double maxSpeed)
        {
            foreach (var action in this)
            {
                action.SetMaxSpeed(maxSpeed);
            }
        }

        private int NudgePoints(int maxIterations, out int nbNudges)
        {
            nbNudges = 0;
            for (int iteration = 0; iteration < maxIterations; iteration++)
            {
                bool anyImprovement = false;

                foreach (var action in this)
                {
                    if (action.Nudge())
                    {
                        nbNudges++;
                        anyImprovement = true;
                    }
                }

                if (!anyImprovement)
                {
                    // Stop if no improvements were made in this iteration
                    return iteration;
                }
            }
            return maxIterations;
        }

        public FunscriptAction[] ToFunscriptActions()
        {
            NudgePoints(100, out var nbNudges);

            var result = new List<FunscriptAction>();
            foreach (var wipAction in this)
            {
                if (wipAction.OriginalActions.Length == 1)
                {
                    // If there's only one original action, simply use the new position
                    result.Add(new FunscriptAction
                    {
                        At = wipAction.OriginalActions[0].At,
                        Pos = wipAction.NewPos
                    });
                }
                else
                {
                    // If there are multiple original actions, scale them linearly
                    int oldStartPos = wipAction.PreviousAction?.OriginalPos ?? wipAction.OriginalActions[0].Pos;
                    int oldEndPos = wipAction.OriginalPos;
                    int newStartPos = wipAction.PreviousAction?.NewPos ?? wipAction.OriginalActions[0].Pos;
                    int newEndPos = wipAction.NewPos;

                    double oldDistance = oldEndPos - oldStartPos;
                    double newDistance = newEndPos - newStartPos;

                    foreach (var originalAction in wipAction.OriginalActions)
                    {
                        double relativePosition = (originalAction.Pos - oldStartPos) / oldDistance;
                        int newPos = (int)Math.Round(newStartPos + relativePosition * newDistance);
                        newPos = Math.Max(0, Math.Min(100, newPos)); // Ensure position is within 0-100 range

                        result.Add(new FunscriptAction
                        {
                            At = originalAction.At,
                            Pos = newPos
                        });
                    }
                }
            }

            return result.ToArray();
        }
    }
}
