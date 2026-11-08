using System;
using System.Collections.Generic;
using System.Text;

namespace PoolsCoop.Net
{
    public enum MsgType : byte
    {
        Hello = 1,    // client -> host: join request
        Welcome = 2,  // host -> client: you are player N, host is in scene S
        Pose = 3,     // both ways, unreliable, newest wins
        State = 4,    // host -> clients, repeated: scene + player list (idempotent, so loss is harmless)
        Ping = 5,
        Pong = 6,
        Bye = 7,
        BodyClaim = 8,    // client -> host: "I touched body X, I have seen epoch E of it"
        BodyGrant = 9,    // host -> all: body X is now owned by P at epoch E (idempotent, repeated)
        BodyState = 10,   // owner -> host -> others: position, rotation, velocities of body X
        BodyRelease = 11, // owner -> host: body X came to rest, nobody needs to stream it
        GameEvent = 12,   // a scripted event fired for one player (scare, trigger); everyone else replays it
        Fx = 13,          // a one-shot effect at a place (footstep, splash); unreliable, sent once, relayed by the host
    }

    /// <summary>Kinds of Fx messages.</summary>
    public static class FxKind
    {
        public const byte Footstep = 1;   // A = volume, B = water depth, C = surface material, D = step direction
        public const byte BodySplash = 2; // A = speed, B = 1 enter / 0 exit
        public const byte BigSplash = 3;  // A = air time, C = splash type
        public const byte HandSplash = 4; // A = speed, B = 1 enter / 0 exit
    }

    /// <summary>One effect: kind, the player it happened to, where, and a few numbers whose meaning depends on the kind.</summary>
    public struct FxEvent
    {
        public byte Kind;
        public byte From;
        public Vec3 Pos;
        public float A, B;
        public byte C, D;

        public bool IsSane => Pos.IsSane && Vec3.Ok(A) && Vec3.Ok(B);
    }

    public sealed class PlayerInfo
    {
        public byte Id;
        public string Name;

        public PlayerInfo(byte id, string name) { Id = id; Name = name; }
    }

    public sealed class PacketWriter
    {
        private readonly byte[] _buf = new byte[Wire.MaxPacket];
        private int _pos;

        public PacketWriter(MsgType type)
        {
            U16(Wire.Magic);
            U8(Wire.Version);
            U8((byte)type);
        }

        private void Need(int n)
        {
            if (_pos + n > _buf.Length) throw new InvalidOperationException("packet too large");
        }

        public PacketWriter U8(byte v) { Need(1); _buf[_pos++] = v; return this; }

        public PacketWriter U16(ushort v)
        {
            Need(2);
            _buf[_pos++] = (byte)v;
            _buf[_pos++] = (byte)(v >> 8);
            return this;
        }

        public PacketWriter U32(uint v)
        {
            Need(4);
            for (int i = 0; i < 4; i++) _buf[_pos++] = (byte)(v >> (8 * i));
            return this;
        }

        public PacketWriter F32(float v) => U32(unchecked((uint)BitConverter.SingleToInt32Bits(v)));

        public PacketWriter Str(string s, int maxBytes)
        {
            byte[] b = Encoding.UTF8.GetBytes(s ?? "");
            int n = Math.Min(b.Length, Math.Min(255, maxBytes));
            Need(1 + n);
            _buf[_pos++] = (byte)n;
            Buffer.BlockCopy(b, 0, _buf, _pos, n);
            _pos += n;
            return this;
        }

        public PacketWriter Pose(NetPose p)
        {
            return F32(p.Pos.X).F32(p.Pos.Y).F32(p.Pos.Z).F32(p.Rot.X).F32(p.Rot.Y).F32(p.Rot.Z).F32(p.Rot.W);
        }

        public PacketWriter Player(PlayerPose p)
        {
            Pose(p.Head).Pose(p.Left).Pose(p.Right);
            // Extension (protocol 1, newer senders): flags, skin tone, five finger bends per hand. Older readers ignore trailing bytes.
            U8((byte)(p.Flags | PoseFlags.Extended)).U8(p.Skin);
            for (int i = 0; i < 5; i++) U8(p.LeftFingers[i]);
            for (int i = 0; i < 5; i++) U8(p.RightFingers[i]);
            return this;
        }

        public PacketWriter Body(BodyState b)
        {
            return Pose(new NetPose(b.Pos, b.Rot)).F32(b.Vel.X).F32(b.Vel.Y).F32(b.Vel.Z).F32(b.AngVel.X).F32(b.AngVel.Y).F32(b.AngVel.Z);
        }

        public byte[] ToArray()
        {
            var r = new byte[_pos];
            Buffer.BlockCopy(_buf, 0, r, 0, _pos);
            return r;
        }
    }

    /// <summary>Reads a packet. Every read is bounds checked and throws FormatException on a short or malformed packet.</summary>
    public sealed class PacketReader
    {
        private readonly byte[] _b;
        private readonly int _len;
        private int _pos;

        internal PacketReader(byte[] b, int len, int pos) { _b = b; _len = len; _pos = pos; }

        private void Need(int n)
        {
            if (n < 0 || _pos + n > _len) throw new FormatException("truncated packet");
        }

        public byte U8() { Need(1); return _b[_pos++]; }

        public ushort U16()
        {
            Need(2);
            ushort v = (ushort)(_b[_pos] | (_b[_pos + 1] << 8));
            _pos += 2;
            return v;
        }

        public uint U32()
        {
            Need(4);
            uint v = 0;
            for (int i = 0; i < 4; i++) v |= (uint)_b[_pos + i] << (8 * i);
            _pos += 4;
            return v;
        }

        public float F32() => BitConverter.Int32BitsToSingle(unchecked((int)U32()));

        public string Str()
        {
            int n = U8();
            Need(n);
            string s = Encoding.UTF8.GetString(_b, _pos, n);
            _pos += n;
            return s;
        }

        public NetPose Pose()
        {
            var p = new NetPose();
            p.Pos = new Vec3(F32(), F32(), F32());
            p.Rot = new Quat(F32(), F32(), F32(), F32());
            return p;
        }

        public PlayerPose Player()
        {
            var p = new PlayerPose();
            p.Head = Pose();
            p.Left = Pose();
            p.Right = Pose();
            if (_len - _pos >= 12)
            {
                p.Flags = U8();
                p.Skin = U8();
                for (int i = 0; i < 5; i++) p.LeftFingers[i] = U8();
                for (int i = 0; i < 5; i++) p.RightFingers[i] = U8();
            }
            return p;
        }

        public int Remaining => _len - _pos;

        public BodyState Body()
        {
            NetPose np = Pose();
            var b = new BodyState();
            b.Pos = np.Pos;
            b.Rot = np.Rot;
            b.Vel = new Vec3(F32(), F32(), F32());
            b.AngVel = new Vec3(F32(), F32(), F32());
            return b;
        }
    }

    public static class Wire
    {
        public const ushort Magic = 0x4C50; // "PL"
        public const byte Version = 1;
        public const int MaxPacket = 1200;
        public const int MaxName = 24;
        public const int MaxScene = 64;
        public const int MaxPlayers = 4;

        /// <summary>Checks the header and returns a reader positioned after it. Never throws.</summary>
        public static bool TryOpen(byte[] data, int len, out MsgType type, out PacketReader reader)
        {
            type = 0;
            reader = null;
            if (data == null || len < 4 || len > MaxPacket || len > data.Length) return false;
            if ((ushort)(data[0] | (data[1] << 8)) != Magic) return false;
            if (data[2] != Version) return false;
            byte t = data[3];
            if (t < (byte)MsgType.Hello || t > (byte)MsgType.Fx) return false;
            type = (MsgType)t;
            reader = new PacketReader(data, len, 4);
            return true;
        }

        public static byte[] EncodeHello(string name) =>
            new PacketWriter(MsgType.Hello).Str(name, MaxName).ToArray();

        public static byte[] EncodeWelcome(byte playerId, byte maxPlayers, string scene) =>
            new PacketWriter(MsgType.Welcome).U8(playerId).U8(maxPlayers).Str(scene, MaxScene).ToArray();

        public static byte[] EncodePose(byte playerId, ushort seq, uint timeMs, PlayerPose pose) =>
            new PacketWriter(MsgType.Pose).U8(playerId).U16(seq).U32(timeMs).Player(pose).ToArray();

        public static byte[] EncodeState(string scene, ushort epoch, IList<PlayerInfo> players)
        {
            var w = new PacketWriter(MsgType.State).Str(scene, MaxScene).U16(epoch).U8((byte)players.Count);
            foreach (var p in players) w.U8(p.Id).Str(p.Name, MaxName);
            return w.ToArray();
        }

        public static byte[] EncodePing(uint timeMs) => new PacketWriter(MsgType.Ping).U32(timeMs).ToArray();

        public static byte[] EncodePong(uint echoMs) => new PacketWriter(MsgType.Pong).U32(echoMs).ToArray();

        public static byte[] EncodeBye() => new PacketWriter(MsgType.Bye).ToArray();

        public static byte[] EncodeGameEvent(uint eventId, byte kind, byte from, ushort seq) =>
            new PacketWriter(MsgType.GameEvent).U32(eventId).U8(kind).U8(from).U16(seq).ToArray();

        public static byte[] EncodeFx(FxEvent e) =>
            new PacketWriter(MsgType.Fx).U8(e.Kind).U8(e.From).F32(e.Pos.X).F32(e.Pos.Y).F32(e.Pos.Z).F32(e.A).F32(e.B).U8(e.C).U8(e.D).ToArray();

        public static FxEvent ReadFx(PacketReader r)
        {
            var e = new FxEvent();
            e.Kind = r.U8();
            e.From = r.U8();
            e.Pos = new Vec3(r.F32(), r.F32(), r.F32());
            e.A = r.F32();
            e.B = r.F32();
            e.C = r.U8();
            e.D = r.U8();
            return e;
        }

        public static byte[] EncodeBodyClaim(uint bodyId, ushort epochSeen) =>
            new PacketWriter(MsgType.BodyClaim).U32(bodyId).U16(epochSeen).ToArray();

        public static byte[] EncodeBodyGrant(uint bodyId, byte owner, ushort epoch) =>
            new PacketWriter(MsgType.BodyGrant).U32(bodyId).U8(owner).U16(epoch).ToArray();

        public static byte[] EncodeBodyRelease(uint bodyId, ushort epoch) =>
            new PacketWriter(MsgType.BodyRelease).U32(bodyId).U16(epoch).ToArray();

        public static byte[] EncodeBodyState(uint bodyId, byte owner, ushort epoch, ushort seq, uint timeMs, BodyState state) =>
            new PacketWriter(MsgType.BodyState).U32(bodyId).U8(owner).U16(epoch).U16(seq).U32(timeMs).Body(state).ToArray();
    }
}
