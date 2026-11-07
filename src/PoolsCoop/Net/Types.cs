using System;

namespace PoolsCoop.Net
{
    // Plain value types so the whole net layer has no dependency on UnityEngine and can be tested without the game.

    public struct Vec3
    {
        public float X, Y, Z;

        public Vec3(float x, float y, float z) { X = x; Y = y; Z = z; }

        public float Distance(Vec3 o)
        {
            float dx = X - o.X, dy = Y - o.Y, dz = Z - o.Z;
            return (float)Math.Sqrt(dx * dx + dy * dy + dz * dz);
        }

        public bool IsSane => Ok(X) && Ok(Y) && Ok(Z);

        public static Vec3 Lerp(Vec3 a, Vec3 b, double f)
        {
            return new Vec3((float)(a.X + (b.X - a.X) * f), (float)(a.Y + (b.Y - a.Y) * f), (float)(a.Z + (b.Z - a.Z) * f));
        }

        internal static bool Ok(float f) => !float.IsNaN(f) && !float.IsInfinity(f) && Math.Abs(f) < 100000f;
    }

    public struct Quat
    {
        public float X, Y, Z, W;

        public Quat(float x, float y, float z, float w) { X = x; Y = y; Z = z; W = w; }

        /// <summary>Shortest-path spherical interpolation, normalised.</summary>
        public static Quat Slerp(Quat a, Quat b, double f)
        {
            double dot = (double)a.X * b.X + (double)a.Y * b.Y + (double)a.Z * b.Z + (double)a.W * b.W;
            double bx = b.X, by = b.Y, bz = b.Z, bw = b.W;
            if (dot < 0) { dot = -dot; bx = -bx; by = -by; bz = -bz; bw = -bw; }
            double sa, sb;
            if (dot > 0.9995) { sa = 1 - f; sb = f; }
            else
            {
                double theta = Math.Acos(Math.Min(1.0, dot));
                double sin = Math.Sin(theta);
                sa = Math.Sin((1 - f) * theta) / sin;
                sb = Math.Sin(f * theta) / sin;
            }
            double x = sa * a.X + sb * bx, y = sa * a.Y + sb * by, z = sa * a.Z + sb * bz, w = sa * a.W + sb * bw;
            double len = Math.Sqrt(x * x + y * y + z * z + w * w);
            if (len < 1e-9) return a;
            return new Quat((float)(x / len), (float)(y / len), (float)(z / len), (float)(w / len));
        }

        public bool IsSane
        {
            get
            {
                if (!Vec3.Ok(X) || !Vec3.Ok(Y) || !Vec3.Ok(Z) || !Vec3.Ok(W)) return false;
                double len = Math.Sqrt((double)X * X + (double)Y * Y + (double)Z * Z + (double)W * W);
                return len > 0.9 && len < 1.1;
            }
        }
    }

    public struct NetPose
    {
        public Vec3 Pos;
        public Quat Rot;

        public NetPose(Vec3 pos, Quat rot) { Pos = pos; Rot = rot; }

        public bool IsSane => Pos.IsSane && Rot.IsSane;
    }

    /// <summary>Full motion state of one rigid body (a door, a rubber duck): where it is and how it moves.</summary>
    public struct BodyState
    {
        public Vec3 Pos;
        public Quat Rot;
        public Vec3 Vel;
        public Vec3 AngVel;

        public bool IsSane => Pos.IsSane && Rot.IsSane && Vel.IsSane && AngVel.IsSane;
    }

    /// <summary>Head plus both hands, in world space.</summary>
    public struct PlayerPose
    {
        public NetPose Head, Left, Right;

        public bool IsSane => Head.IsSane && Left.IsSane && Right.IsSane;

        /// <summary>Largest position difference between the two poses over head and both hands.</summary>
        public float MaxPosDistance(PlayerPose o)
        {
            float a = Head.Pos.Distance(o.Head.Pos);
            float b = Left.Pos.Distance(o.Left.Pos);
            float c = Right.Pos.Distance(o.Right.Pos);
            return Math.Max(a, Math.Max(b, c));
        }
    }
}
