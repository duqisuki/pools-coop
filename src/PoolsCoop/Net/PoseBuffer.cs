using System;
using System.Collections.Generic;

namespace PoolsCoop.Net
{
    /// <summary>
    /// Snapshot interpolation for one remote player. Each pose carries the sender's own millisecond clock. We never compare clocks
    /// directly; we track (arrival time - sender time), whose minimum over the last two seconds is the clock offset plus the fastest
    /// network delay, and every arrival above that minimum is jitter. The player is drawn <see cref="DelayMs"/> behind the newest data,
    /// where DelayMs follows the observed jitter, so the picture is smooth, not teleporting, and as fresh as the network allows.
    /// </summary>
    public sealed class PoseBuffer
    {
        private struct Snap
        {
            public double T;
            public PlayerPose P;
        }

        private const int MaxSnaps = 150;
        private const int JitterWindow = 160;
        private const double MaxExtrapolateMs = 120;

        private readonly List<Snap> _snaps = new List<Snap>(MaxSnaps + 1);
        private readonly Queue<KeyValuePair<long, double>> _offsets = new Queue<KeyValuePair<long, double>>();
        private readonly double[] _jitter = new double[JitterWindow];
        private int _jitterCount;
        private int _jitterNext;
        private double _minOffset;
        private bool _hasOffset;
        private double _intervalMs = 33;

        public double MinDelayMs = 35;
        public double MaxDelayMs = 220;

        /// <summary>How far behind the newest data we draw, in ms. Adapts to jitter.</summary>
        public double DelayMs { get; private set; } = 60;

        /// <summary>Sender-clock time of the pose returned by the last Sample call.</summary>
        public double LastRenderSenderMs { get; private set; }

        public int Count => _snaps.Count;

        public void Add(uint senderMs, long recvMs, PlayerPose pose)
        {
            double t = senderMs;

            int i = _snaps.Count;
            while (i > 0 && _snaps[i - 1].T > t) i--;
            if (i > 0 && _snaps[i - 1].T == t) return; // duplicate
            _snaps.Insert(i, new Snap { T = t, P = pose });
            if (_snaps.Count > MaxSnaps) _snaps.RemoveAt(0);

            if (i == _snaps.Count - 1 && _snaps.Count >= 2)
            {
                double dt = t - _snaps[_snaps.Count - 2].T;
                if (dt > 1 && dt < 500) _intervalMs += (dt - _intervalMs) * 0.05;
            }

            double off = recvMs - t;
            _offsets.Enqueue(new KeyValuePair<long, double>(recvMs, off));
            while (_offsets.Count > 0 && recvMs - _offsets.Peek().Key > 2000) _offsets.Dequeue();
            double min = double.MaxValue;
            foreach (var kv in _offsets) if (kv.Value < min) min = kv.Value;
            _minOffset = min;
            _hasOffset = true;

            _jitter[_jitterNext] = Math.Max(0, off - min);
            _jitterNext = (_jitterNext + 1) % JitterWindow;
            if (_jitterCount < JitterWindow) _jitterCount++;

            if (_jitterCount >= 20)
            {
                var tmp = new double[_jitterCount];
                Array.Copy(_jitter, tmp, _jitterCount);
                Array.Sort(tmp);
                double p90 = tmp[(int)(0.9 * (tmp.Length - 1))];
                double target = _intervalMs * 1.5 + p90;
                target = Math.Max(MinDelayMs, Math.Min(MaxDelayMs, target));
                DelayMs += (target - DelayMs) * 0.1;
            }
        }

        // Catch-up after a stall: when the interpolation target itself jumps (the sender froze and then moved on), keep drawing where
        // we were and fade the difference out, instead of popping. A jump of 3 m or more is a teleport and is shown at once.
        private const double JumpMinM = 0.12;
        private const double TeleportM = 3.0;
        private const double CatchUpMs = 200;
        private bool _hasShown;
        private long _lastSampleMs;
        private PlayerPose _lastTarget;
        private PlayerPose _shown;
        private double _ohx, _ohy, _ohz, _olx, _oly, _olz, _orx, _ory, _orz;

        /// <summary>The pose to draw now. localNowMs must use the same clock as the recvMs given to Add.</summary>
        public bool Sample(long localNowMs, out PlayerPose pose)
        {
            if (!SampleRaw(localNowMs, out PlayerPose target)) { pose = default(PlayerPose); return false; }

            if (_hasShown)
            {
                double dt = Math.Max(0, localNowMs - _lastSampleMs);
                double jump = Math.Max(target.Head.Pos.Distance(_lastTarget.Head.Pos),
                              Math.Max(target.Left.Pos.Distance(_lastTarget.Left.Pos), target.Right.Pos.Distance(_lastTarget.Right.Pos)));
                if (jump >= TeleportM)
                {
                    _ohx = _ohy = _ohz = _olx = _oly = _olz = _orx = _ory = _orz = 0;
                }
                else if (jump > JumpMinM)
                {
                    _ohx = _shown.Head.Pos.X - target.Head.Pos.X; _ohy = _shown.Head.Pos.Y - target.Head.Pos.Y; _ohz = _shown.Head.Pos.Z - target.Head.Pos.Z;
                    _olx = _shown.Left.Pos.X - target.Left.Pos.X; _oly = _shown.Left.Pos.Y - target.Left.Pos.Y; _olz = _shown.Left.Pos.Z - target.Left.Pos.Z;
                    _orx = _shown.Right.Pos.X - target.Right.Pos.X; _ory = _shown.Right.Pos.Y - target.Right.Pos.Y; _orz = _shown.Right.Pos.Z - target.Right.Pos.Z;
                }
                double k = Math.Exp(-dt / CatchUpMs);
                _ohx *= k; _ohy *= k; _ohz *= k; _olx *= k; _oly *= k; _olz *= k; _orx *= k; _ory *= k; _orz *= k;
            }

            _lastTarget = target;
            pose = target;
            pose.Head.Pos = new Vec3((float)(target.Head.Pos.X + _ohx), (float)(target.Head.Pos.Y + _ohy), (float)(target.Head.Pos.Z + _ohz));
            pose.Left.Pos = new Vec3((float)(target.Left.Pos.X + _olx), (float)(target.Left.Pos.Y + _oly), (float)(target.Left.Pos.Z + _olz));
            pose.Right.Pos = new Vec3((float)(target.Right.Pos.X + _orx), (float)(target.Right.Pos.Y + _ory), (float)(target.Right.Pos.Z + _orz));
            _shown = pose;
            _hasShown = true;
            _lastSampleMs = localNowMs;
            return true;
        }

        private bool SampleRaw(long localNowMs, out PlayerPose pose)
        {
            pose = default(PlayerPose);
            if (!_hasOffset || _snaps.Count == 0) return false;

            double tr = localNowMs - _minOffset - DelayMs;
            LastRenderSenderMs = tr;

            Snap first = _snaps[0];
            if (tr <= first.T) { pose = first.P; return true; }

            Snap last = _snaps[_snaps.Count - 1];
            if (tr >= last.T)
            {
                pose = last.P;
                if (_snaps.Count >= 2)
                {
                    Snap prev = _snaps[_snaps.Count - 2];
                    double span = last.T - prev.T;
                    double ahead = Math.Min(tr - last.T, MaxExtrapolateMs);
                    if (span > 0 && span < 300 && ahead > 0)
                    {
                        pose.Head.Pos = Extrapolate(prev.P.Head.Pos, last.P.Head.Pos, span, ahead);
                        pose.Left.Pos = Extrapolate(prev.P.Left.Pos, last.P.Left.Pos, span, ahead);
                        pose.Right.Pos = Extrapolate(prev.P.Right.Pos, last.P.Right.Pos, span, ahead);
                    }
                }
                return true;
            }

            for (int i = _snaps.Count - 1; i > 0; i--)
            {
                if (_snaps[i - 1].T <= tr)
                {
                    Snap a = _snaps[i - 1], b = _snaps[i];
                    double f = (tr - a.T) / (b.T - a.T);
                    pose = Blend(a.P, b.P, f);
                    return true;
                }
            }
            pose = first.P;
            return true;
        }

        private static Vec3 Extrapolate(Vec3 a, Vec3 b, double spanMs, double aheadMs)
        {
            double k = aheadMs / spanMs;
            return new Vec3((float)(b.X + (b.X - a.X) * k), (float)(b.Y + (b.Y - a.Y) * k), (float)(b.Z + (b.Z - a.Z) * k));
        }

        private static NetPose BlendOne(NetPose a, NetPose b, double f)
        {
            return new NetPose(Vec3.Lerp(a.Pos, b.Pos, f), Quat.Slerp(a.Rot, b.Rot, f));
        }

        private static PlayerPose Blend(PlayerPose a, PlayerPose b, double f)
        {
            var p = new PlayerPose();
            p.Head = BlendOne(a.Head, b.Head, f);
            p.Left = BlendOne(a.Left, b.Left, f);
            p.Right = BlendOne(a.Right, b.Right, f);
            return p;
        }
    }
}
