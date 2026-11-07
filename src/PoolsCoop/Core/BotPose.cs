using System;
using PoolsCoop.Net;

namespace PoolsCoop.Core
{
    /// <summary>
    /// Deterministic synthetic player motion for bot tests: head walks a circle, hands swing.
    /// The receiver can recompute the exact expected pose from (timeMs, playerId), so any corruption,
    /// player mix-up or stale pose is detected without a human looking at anything.
    /// </summary>
    public static class BotPose
    {
        public static PlayerPose At(uint timeMs, int playerId)
        {
            double t = timeMs / 1000.0;
            double a = t * 0.5 + playerId * 1.7;
            double cx = Math.Cos(a) * 1.5 + playerId * 2.0;
            double cz = Math.Sin(a) * 1.5;
            double hy = 1.6 + 0.05 * Math.Sin(t * 2.0);
            double yaw = a + Math.PI / 2.0;

            Quat rot = Yaw(yaw);
            var head = new NetPose(new Vec3((float)cx, (float)hy, (float)cz), rot);
            var left = new NetPose(HandPos(cx, hy, cz, yaw, -0.25, 0.15 * Math.Sin(t * 3.0)), rot);
            var right = new NetPose(HandPos(cx, hy, cz, yaw, 0.25, 0.15 * Math.Sin(t * 3.0 + 1.0)), rot);

            var p = new PlayerPose();
            p.Head = head;
            p.Left = left;
            p.Right = right;
            return p;
        }

        private static Vec3 HandPos(double hx, double hy, double hz, double yaw, double side, double swing)
        {
            double ox = side, oy = -0.5 + swing, oz = 0.3;
            double c = Math.Cos(yaw), s = Math.Sin(yaw);
            double x = ox * c + oz * s;
            double z = -ox * s + oz * c;
            return new Vec3((float)(hx + x), (float)(hy + oy), (float)(hz + z));
        }

        private static Quat Yaw(double yaw)
        {
            double h = yaw * 0.5;
            return new Quat(0f, (float)Math.Sin(h), 0f, (float)Math.Cos(h));
        }
    }
}
