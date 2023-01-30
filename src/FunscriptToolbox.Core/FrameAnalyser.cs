using System;
using System.Collections.Generic;
using System.Linq;

namespace FunscriptToolbox.Core
{
    public abstract class FrameAnalyser
    {
        public abstract string Name { get; }
        public List<long> FrameResults { get; }
        public List<FunscriptAction> Actions { get; }
        public int Percentage => (100 * m_nbPointsInActions) / this.FrameResults.Count;

        private long? m_lastAction;
        private TimeSpan m_startTimeFrame;
        private TimeSpan m_endTimeFrame;
        private TimeSpan m_previousTimeFrame;
        private int m_nbActionsInRow;
        private int m_nbPointsInActions;

        public FrameAnalyser()
        {
            this.FrameResults = new List<long>();
            this.Actions = new List<FunscriptAction>();
            m_lastAction = null;
            m_startTimeFrame = TimeSpan.Zero;
            m_endTimeFrame = TimeSpan.Zero;
            m_previousTimeFrame = TimeSpan.Zero;
            m_nbActionsInRow = 0;
            m_nbPointsInActions = 0;
        }          

        private void AddWithoutDuplicate(int at, int pos)
        {
            var lastAction = this.Actions.LastOrDefault();
            if (lastAction?.At == at && lastAction?.Pos != pos)
            {
                throw new Exception("What?");
            }

            if (lastAction == null || lastAction.At != at)
            {
                this.Actions.Add(new FunscriptAction { At = at, Pos = pos });
            }
        }

        public void AddFrameData(MotionVectorsFrame frame)
        {
            var value = ComputeFrameValue(frame);
            FrameResults.Add(value);

            if ((value > 0 && m_lastAction > 0) || (value < 0 && m_lastAction < 0))
            {
                m_nbActionsInRow++;
                m_endTimeFrame = frame.FrameTimeInMs;
            }
            else 
            {
                if (m_nbActionsInRow >= 12)
                {
                    m_nbPointsInActions += m_nbActionsInRow;
                    if (m_lastAction > 0)
                    {
                        AddWithoutDuplicate((int)m_startTimeFrame.TotalMilliseconds, 100);
                        AddWithoutDuplicate((int)m_endTimeFrame.TotalMilliseconds, 0);
                    }
                    else
                    {
                        AddWithoutDuplicate((int)m_startTimeFrame.TotalMilliseconds, 0);
                        AddWithoutDuplicate((int)m_endTimeFrame.TotalMilliseconds, 100);
                    }
                }

                m_lastAction = value;
                m_startTimeFrame = m_previousTimeFrame;
                m_nbActionsInRow = 1;
            }
            m_previousTimeFrame = frame.FrameTimeInMs;
        }

        protected abstract long ComputeFrameValue(MotionVectorsFrame frame);
    }
}
