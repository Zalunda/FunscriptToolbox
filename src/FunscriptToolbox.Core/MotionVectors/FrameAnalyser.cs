using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core.MotionVectors
{

    public class FrameAnalyser
    {
        public List<long> FrameResults { get; }
        public int Percentage => 100 * m_nbPointsInActions / this.FrameResults.Count;

        private long? m_lastAction;
        private List<WorkingFunscriptAction> r_workingFunscriptActions;
        private TimeSpan m_startTimeFrame;
        private TimeSpan m_endTimeFrame;
        private TimeSpan m_previousTimeFrame;
        private List<long> m_nbActionsInRow;
        private int m_nbPointsInActions;

        public BlocAnalyserRule[] Rules { get; }

        public FrameAnalyser(BlocAnalyserRule[] rules = null)
        {
            this.FrameResults = new List<long>();
            r_workingFunscriptActions = new List<WorkingFunscriptAction>();
            m_lastAction = null;
            m_startTimeFrame = TimeSpan.Zero;
            m_endTimeFrame = TimeSpan.Zero;
            m_previousTimeFrame = TimeSpan.Zero;
            m_nbActionsInRow = new List<long>();
            m_nbPointsInActions = 0;
            this.Rules = rules;
        }

        public void AddFrameData(MotionVectorsFrame frame)
        {
            var result = ComputeFrameTotalWeight(frame);
            FrameResults.Add(result);

            if (m_previousTimeFrame == TimeSpan.Zero)
            {
                m_previousTimeFrame = frame.FrameTimeInMs;
            }

            if ((result > 0 && m_lastAction > 0) || (result < 0 && m_lastAction < 0))
            {
                m_nbActionsInRow.Add(result);
                m_endTimeFrame = frame.FrameTimeInMs;
            }
            else
            {
                if (m_nbActionsInRow.Count >= 10) // TODO adjust
                {
                    m_nbPointsInActions += m_nbActionsInRow.Count;
                    if (m_lastAction > 0)
                    {
                        AddWithoutDuplicate((int)m_startTimeFrame.TotalMilliseconds, 100);
                        AddWithoutDuplicate((int)m_endTimeFrame.TotalMilliseconds, 0, m_nbActionsInRow);
                    }
                    else
                    {
                        AddWithoutDuplicate((int)m_startTimeFrame.TotalMilliseconds, 0);
                        AddWithoutDuplicate((int)m_endTimeFrame.TotalMilliseconds, 100, m_nbActionsInRow);
                    }
                    m_nbActionsInRow.Clear();
                }

                m_lastAction = result;
                m_startTimeFrame = m_previousTimeFrame;

                if (m_nbActionsInRow.Count > 0)
                {
                    //Console.WriteLine($"???, {m_nbActionsInRow.Count,3} unused: {string.Join("|", m_nbActionsInRow.Take(5).Select(r => r.ToString()))}");
                }
                m_nbActionsInRow.Clear();
                m_nbActionsInRow.Add(result);
            }
            m_previousTimeFrame = frame.FrameTimeInMs;
        }

        private void AddWithoutDuplicate(int at, int pos, List<long> results = null)
        {
            var lastAction = r_workingFunscriptActions.LastOrDefault();
            if (lastAction?.At == at && lastAction?.Pos != pos)
            {
                throw new Exception("What?");
            }

            if (lastAction == null || lastAction.At != at)
            {
                r_workingFunscriptActions.Add(new WorkingFunscriptAction(at, pos, results));
                //if (pos == 100)
                    //Console.WriteLine(r_workingFunscriptActions.Last().ToString());
            }
            else
            {
                if (results?.Count > 0)
                {
                    throw new Exception("What #2?");
                }
            }
        }

        public FunscriptAction[] GetFinalActions()
        {
            var sortedWeight = r_workingFunscriptActions.Where(f => f.Pos == 100).Select(f => Math.Abs(f.WeightPerFrame)).OrderBy(w => w).ToArray();
            var targetWeigth = (sortedWeight.Length > 0) ? sortedWeight[sortedWeight.Length * 9 / 10] : 0;

            var actions = new List<FunscriptAction>();
            foreach (var action in r_workingFunscriptActions)
            {
                if (action.Pos == 100)
                {
                    var newPos = Math.Min(100, 50 + ((int)((double)Math.Abs(action.WeightPerFrame) / targetWeigth * 50)));
                    actions.Add(new FunscriptAction { At = action.At, Pos = newPos });
                }
                else
                {
                    actions.Add(new FunscriptAction { At = action.At, Pos = action.Pos });
                }
            }
            return actions.ToArray();
        }


        private class WorkingFunscriptAction
        {
            public int At { get; }
            public int Pos { get; }
            public int NbResults { get; }
            public long TotalWeight { get; }
            public long WeightPerFrame { get; }

            public WorkingFunscriptAction(int at, int pos, List<long> results)
            {
                At = at;
                Pos = pos;
                if (results == null)
                {
                    NbResults = 0;
                    TotalWeight = 0;
                    WeightPerFrame = 0;
                }
                else
                {
                    NbResults = results.Count;
                    TotalWeight = results.Sum(f => f);
                    WeightPerFrame = results.Count == 0 ? 0 : TotalWeight / results.Count;
                }
            }

            public override string ToString()
            {
                return $"{Pos,3}, {NbResults,3}, {TotalWeight,12}, {WeightPerFrame,12}, {At}";
            }
        }

        protected virtual long ComputeFrameTotalWeight(MotionVectorsFrame frame)
        {
            var lookup = MotionVectorsHelper.GetLookupMotionXYAndAngleToWeightTable();

            // Note: Much faster to loop than using linq\Sum.
            long total = 0;
            foreach (var rule in this.Rules)
            {
                total += lookup[frame.MotionsX[rule.Index], frame.MotionsY[rule.Index], rule.Direction];
            }
            return total;
        }
    }
}
