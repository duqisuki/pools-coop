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

    /// <summary>How far each finger of one hand is bent, 0 = open, 255 = fist.</summary>
    public struct FingerBends
    {
        public byte Thumb, Index, Middle, Ring, Pinky;

        public FingerBends(byte thumb, byte index, byte middle, byte ring, byte pinky)
        {
            Thumb = thumb; Index = index; Middle = middle; Ring = ring; Pinky = pinky;
        }

        public static FingerBends All(byte v) => new FingerBends(v, v, v, v, v);

        /// <summary>0 thumb, 1 index, 2 middle, 3 ring, 4 pinky.</summary>
        public byte this[int i]
        {
            get
            {
                switch (i) { case 0: return Thumb; case 1: return Index; case 2: return Middle; case 3: return Ring; default: return Pinky; }
            }
            set
            {
                switch (i) { case 0: Thumb = value; break; case 1: Index = value; break; case 2: Middle = value; break; case 3: Ring = value; break; default: Pinky = value; break; }
            }
        }

        public static FingerBends Lerp(FingerBends a, FingerBends b, double f)
        {
            var r = new FingerBends();
            for (int i = 0; i < 5; i++) r[i] = (byte)Math.Round(a[i] + (b[i] - a[i]) * Math.Max(0, Math.Min(1, f)));
            return r;
        }
    }

    /// <summary>Bits of PlayerPose.Flags.</summary>
    public static class PoseFlags
    {
        public const byte Vr = 1;            // the sender plays in a headset (hands are tracked)
        public const byte FemaleHands = 2;   // the sender picked the female hand model in the game's settings
        public const byte Extended = 4;      // the fields after the three poses were on the wire (newer sender)
        public const byte Moving = 8;        // the sender's body is walking (flat-screen hands swing)
        public const byte Swimming = 16;
        public const byte HandsAreRoots = 32; // Left/Right are the hand model's root pose (not a controller pose)
    }

    /// <summary>Head plus both hands, in world space.</summary>
    public struct PlayerPose
    {
        public NetPose Head, Left, Right;
        public FingerBends LeftFingers, RightFingers;
        public byte Flags;
        public byte Skin; // skin tone 0..255 (the game's slider 0..1)

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

namespace PoolsCoop.Net
{
    /// <summary>"Random" numbers every machine agrees on: a hash of the session seed and a key (an object's id).</summary>
    public static class SharedRandom
    {
        public static uint Hash(string text)
        {
            uint h = 2166136261u;
            foreach (char c in text ?? "") { h ^= c; h *= 16777619u; }
            return h;
        }

        /// <summary>A number in [0, 1) that depends only on seed and key (splitmix-style mixing).</summary>
        public static double Unit(uint seed, uint key)
        {
            ulong z = ((ulong)seed << 32 | key) + 0x9E3779B97F4A7C15UL;
            z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
            z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
            z ^= z >> 31;
            return (z >> 11) * (1.0 / (1UL << 53));
        }
    }
}
