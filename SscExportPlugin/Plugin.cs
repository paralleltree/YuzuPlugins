using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

using ConcurrentPriorityQueue;
using Yuzu.Core.Events;
using Yuzu.Core.Track;
using Yuzu.Plugins;

namespace SscExportPlugin
{
    public class SscExportPlugin : IScoreBookExportPlugin
    {
        public string DisplayName => "Shooting Simulation Chart(*.ssc)";

        public string Filter => "Shooting Simulation Chart(*.ssc)|*.ssc";

        public void Export(IScoreBookExportPluginArgs args)
        {
            var book = args.GetScoreBook();
            var buffer = new List<Tuple<int, string>>();
            var bars = new BarIndexCalculator(book.Score.TicksPerBeat, book.Score.Events.TimeSignatureChangeEvents);

            var bpms = book.Score.Events.BPMChangeEvents.OrderBy(p => p.Tick).ToList();
            if (bpms[0].Tick == 0) buffer.Add(Tuple.Create(0, $"*0/0/4,bpm,{bpms[0].BPM}"));
            foreach (var bpm in bpms.Skip(1))
                buffer.Add(Tuple.Create(bpm.Tick, $"{GetTime(bpm.Tick)},bpm,{bpm.BPM}"));

            var sigs = book.Score.Events.TimeSignatureChangeEvents.OrderBy(p => p.Tick).ToList();
            if (sigs[0].Tick == 0) buffer.Add(Tuple.Create(0, $"*0/0/4,beat,{sigs[0].Numerator}/{sigs[0].Denominator}"));
            foreach (var sig in sigs.Skip(1))
                buffer.Add(Tuple.Create(sig.Tick, $"{GetTime(sig.Tick)},beat,{sig.Numerator}/{sig.Denominator}"));

            var highSpeeds = book.Score.Events.HighSpeedChangeEvents.OrderBy(p => p.Tick).ToList();
            foreach (var highSpeed in highSpeeds)
                buffer.Add(Tuple.Create(highSpeed.Tick, $"{GetTime(highSpeed.Tick)},hispeed,{highSpeed.SpeedRatio}"));

            // フィールド
            var sides = new[] { book.Score.Field.Left, book.Score.Field.Right };
            var stepTicks = sides.SelectMany(p => p.FieldWall.Points.Select(q => q.Tick));

            int li = 0;
            int ri = 0;
            var leftSteps = book.Score.Field.Left.FieldWall.Points.OrderBy(p => p.Tick).ToList();
            var rightSteps = book.Score.Field.Right.FieldWall.Points.OrderBy(p => p.Tick).ToList();
            foreach (var tick in stepTicks.Distinct().OrderBy(p => p))
            {
                if (li < leftSteps.Count - 1)
                {
                    if (tick >= leftSteps[li + 1].Tick) li++;
                }
                if (ri < rightSteps.Count - 1)
                {
                    if (tick >= rightSteps[ri + 1].Tick) ri++;
                }

                int left = li < leftSteps.Count - 1 ? GetInterpolated(leftSteps[li], leftSteps[li + 1], tick) : GetOffset(leftSteps[li].LaneOffset);
                int right = ri < rightSteps.Count - 1 ? GetInterpolated(rightSteps[ri], rightSteps[ri + 1], tick) : GetOffset(rightSteps[ri].LaneOffset);
                buffer.Add(Tuple.Create(tick, $"{GetTime(tick)},fieldset,{left},{right},,"));
            }

            WriteField(book.Score.Field.Left, "L");
            WriteField(book.Score.Field.Right, "R");

            var surfaceLanes = book.Score.SurfaceLanes.Select(p => new
            {
                MinTick = p.Points.Min(q => q.Tick),
                MaxTick = p.Points.Max(q => q.Tick),
                Value = p
            }).OrderBy(p => p.MinTick).ToList();

            var allocator = new IdentifierAllocator(Enumerable.Range(0, 30).Select(p => p.ToString()));
            foreach (var lane in surfaceLanes)
            {
                var laneNumber = allocator.Allocate(lane.MinTick, lane.MaxTick - lane.MinTick);
                var points = lane.Value.Points.OrderBy(p => p.Tick).ToList();
                int color = lane.Value.LaneColor == SurfaceLaneColor.Red ? 0 :
                    lane.Value.LaneColor == SurfaceLaneColor.Green ? 1 : 2;
                buffer.Add(Tuple.Create(points[0].Tick, $"{GetTime(points[0].Tick)},laneset,{laneNumber},{color},{GetOffset(points[0].LaneOffset)}"));
                foreach (var point in points.Skip(1).Take(points.Count - 2))
                    buffer.Add(Tuple.Create(point.Tick, $"{GetTime(point.Tick)},lanepos,{laneNumber},{GetOffset(point.LaneOffset)},"));

                foreach (var note in lane.Value.Notes.OrderBy(p => p.TickRange.StartTick))
                {
                    if (note.TickRange.Duration == 0)
                    {
                        // TAP
                        buffer.Add(Tuple.Create(note.TickRange.StartTick, $"{GetTime(note.TickRange.StartTick)},{(note.IsCritical ? "extap" : "tap")},{laneNumber}"));
                    }
                    else
                    {
                        // HOLD
                        buffer.Add(Tuple.Create(note.TickRange.StartTick, $"{GetTime(note.TickRange.StartTick)},{(note.IsCritical ? "exholdset" : "holdset")},{laneNumber}"));
                        buffer.Add(Tuple.Create(note.TickRange.EndTick, $"{GetTime(note.TickRange.EndTick)},holdend,{laneNumber}"));
                    }
                }

                buffer.Add(Tuple.Create(points[points.Count - 1].Tick, $"{GetTime(points[points.Count - 1].Tick)},laneend,{laneNumber},{GetOffset(points[points.Count - 1].LaneOffset)},"));
            }

            foreach (var flick in book.Score.Flicks)
            {
                string direction = flick.Direction == HorizontalDirection.Left ? "left" : "right";
                buffer.Add(Tuple.Create(flick.Position.Tick, $"{GetTime(flick.Position.Tick)},{(flick.IsCritical ? "exflick" : "flick")},{direction},{GetOffset(flick.Position.LaneOffset)}"));
            }

            foreach (var bell in book.Score.Bells)
            {
                buffer.Add(Tuple.Create(bell.Position.Tick, $"{GetTime(bell.Position.Tick)},heal,{GetOffset(bell.Position.LaneOffset)}"));
            }

            foreach (var bullet in book.Score.Bullets)
            {
                buffer.Add(Tuple.Create(bullet.Position.Tick, $"{GetTime(bullet.Position.Tick)},shot,{GetOffset(bullet.Position.LaneOffset)},1,1"));
            }

            using (var writer = new StreamWriter(args.OutputPath, false, Encoding.GetEncoding("shift-jis")))
            {
                writer.WriteLine($"#title {book.Title}");
                writer.WriteLine($"#artist {book.ArtistName}");
                writer.WriteLine($"#notes {book.NotesDesignerName}");
                writer.WriteLine("#datend");

                foreach (var line in buffer.OrderBy(p => p.Item1))
                    writer.WriteLine(line.Item2);

                writer.WriteLine("+0/4,fin");
            }

            void WriteField(FieldSide fs, string side)
            {
                foreach (var guarded in fs.FieldWall.GuardedSections)
                {
                    buffer.Add(Tuple.Create(guarded.StartTick, $"{GetTime(guarded.StartTick)},setwall,{side}"));
                    buffer.Add(Tuple.Create(guarded.EndTick, $"{GetTime(guarded.EndTick)},delwall,{side}"));
                }

                foreach (var guide in fs.SideLanes.Select(p => p.ValidRange))
                {
                    buffer.Add(Tuple.Create(guide.StartTick, $"{GetTime(guide.StartTick)},setnoti,{side}"));
                    buffer.Add(Tuple.Create(guide.EndTick, $"{GetTime(guide.EndTick)},delnoti,{side}"));
                }

                foreach (var note in fs.SideLanes.SelectMany(p => p.Notes))
                {
                    if (note.TickRange.Duration == 0)
                    {
                        buffer.Add(Tuple.Create(note.TickRange.StartTick, $"{GetTime(note.TickRange.StartTick)},{(note.IsCritical ? "extap" : "tap")},{side}"));
                    }
                    else
                    {
                        buffer.Add(Tuple.Create(note.TickRange.StartTick, $"{GetTime(note.TickRange.StartTick)},{(note.IsCritical ? "exholdset" : "holdset")},{side}"));
                        buffer.Add(Tuple.Create(note.TickRange.EndTick, $"{GetTime(note.TickRange.EndTick)},holdend,{side}"));
                    }
                }
            }

            int GetOffset(int offset)
            {
                return 480 * offset / book.Score.HalfHorizontalResolution;
            }

            int GetInterpolated(FieldPoint first, FieldPoint second, int posTick)
            {
                float rate = (float)(posTick - first.Tick) / (second.Tick - first.Tick);
                return (int)(GetOffset(first.LaneOffset) + GetOffset(second.LaneOffset - first.LaneOffset) * rate);
            }

            string GetTime(int tick)
            {
                var pos = bars.GetBarPositionFromTick(tick);
                int gcd = GetGcd(bars.BarTick, pos.TickOffset);
                return $"*{pos.BarIndex + 1}/{pos.TickOffset / gcd}/{bars.BarTick / gcd}";
            }
        }

        protected static int GetGcd(int a, int b)
        {
            if (a < b) return GetGcd(b, a);
            if (b == 0) return a;
            return GetGcd(b, a % b);
        }

        public class IdentifierAllocator
        {
            private IEnumerable<string> identifiers;
            private int lastStartTick;
            private Stack<string> IdentifierStack;
            private ConcurrentPriorityQueue<Tuple<int, string>, int> UsedIdentifiers;

            public IdentifierAllocator(IEnumerable<string> identifiers)
            {
                this.identifiers = identifiers;
                Clear();
            }

            public void Clear()
            {
                lastStartTick = 0;
                IdentifierStack = new Stack<string>(identifiers.Reverse());
                UsedIdentifiers = new ConcurrentPriorityQueue<Tuple<int, string>, int>();
            }

            public string Allocate(int startTick, int duration)
            {
                if (startTick < lastStartTick) throw new InvalidOperationException("startTick must not be less than last called value.");
                while (UsedIdentifiers.Count > 0 && UsedIdentifiers.Peek().Item1 < startTick)
                {
                    IdentifierStack.Push(UsedIdentifiers.Dequeue().Item2);
                }
                string s = IdentifierStack.Pop();
                int endTick = startTick + duration;
                UsedIdentifiers.Enqueue(Tuple.Create(endTick, s), -endTick);
                lastStartTick = startTick;
                return s;
            }
        }

        public class BarIndexCalculator
        {
            public int BarTick { get; private set; }
            private SortedDictionary<int, TimeSignatureItem> timeSignatures;

            /// <summary>
            /// 時間順にソートされた有効な拍子変更イベントのコレクションを取得します。
            /// </summary>
            public IEnumerable<TimeSignatureItem> TimeSignatures
            {
                get { return timeSignatures.Select(p => p.Value).Reverse(); }
            }

            public BarIndexCalculator(int ticksPerBeat, IEnumerable<TimeSignatureChangeEvent> events)
            {
                BarTick = ticksPerBeat * 4;
                var ordered = events.OrderBy(p => p.Tick).ToList();
                var dic = new SortedDictionary<int, TimeSignatureItem>();
                int pos = 0;
                int barIndex = 0;
                for (int i = 0; i < ordered.Count; i++)
                {
                    var item = new TimeSignatureItem()
                    {
                        StartTick = pos,
                        StartBarIndex = barIndex,
                        TimeSignature = ordered[i]
                    };

                    // 時間逆順で追加
                    if (dic.ContainsKey(-pos)) dic[-pos] = item;
                    else dic.Add(-pos, item);

                    if (i < ordered.Count - 1)
                    {
                        int barLength = BarTick * ordered[i].Numerator / ordered[i].Denominator;
                        int duration = ordered[i + 1].Tick - pos;
                        pos += duration / barLength * barLength;
                        barIndex += duration / barLength;
                    }
                }

                timeSignatures = dic;
            }

            public BarPosition GetBarPositionFromTick(int tick)
            {
                foreach (var item in timeSignatures)
                {
                    if (tick < item.Value.StartTick) continue;
                    var sig = item.Value.TimeSignature;
                    int barLength = BarTick * sig.Numerator / sig.Denominator;
                    int tickOffset = tick - item.Value.StartTick;
                    int barOffset = tickOffset / barLength;
                    return new BarPosition()
                    {
                        BarIndex = item.Value.StartBarIndex + barOffset,
                        TickOffset = tickOffset - barOffset * barLength,
                        TimeSignature = item.Value.TimeSignature
                    };
                }

                throw new InvalidOperationException();
            }

            public TimeSignatureChangeEvent GetTimeSignatureFromBarIndex(int barIndex)
            {
                foreach (var item in timeSignatures)
                {
                    if (barIndex < item.Value.StartBarIndex) continue;
                    return item.Value.TimeSignature;
                }

                throw new InvalidOperationException();
            }

            public struct BarPosition
            {
                public int BarIndex { get; set; }
                public int TickOffset { get; set; }
                public TimeSignatureChangeEvent TimeSignature { get; set; }
            }

            public class TimeSignatureItem
            {
                public int StartTick { get; set; }
                public int StartBarIndex { get; set; }
                public TimeSignatureChangeEvent TimeSignature { get; set; }
            }
        }
    }
}
