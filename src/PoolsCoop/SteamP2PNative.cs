using System;
using System.IO;
using System.Runtime.InteropServices;
using PoolsCoop.Net;

namespace PoolsCoop
{
    /// <summary>
    /// Talks to the game's own steam_api64.dll through the flat C API (ISteamNetworking, the old P2P API: the game's DLL has no
    /// NetworkingMessages). The game initialises Steam itself with its real app id; we only start using the interfaces once that
    /// has happened, so Ready stays false for a while after launch. Main thread only.
    /// </summary>
    internal sealed unsafe class SteamP2PNative : ISteamP2P, ISteamSocial
    {
        private const ulong ThisGameAppId = 2663530UL;
        private const int FriendFlagImmediate = 4; // k_EFriendFlagImmediate: real friends only

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamFriends_v017();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern int SteamAPI_ISteamFriends_GetFriendCount(IntPtr self, int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamFriends_GetFriendByIndex(IntPtr self, int index, int flags);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendPersonaName(IntPtr self, ulong id);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetPersonaName(IntPtr self);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamFriends_GetFriendGamePlayed(IntPtr self, ulong id, byte* info);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamFriends_SetRichPresence(IntPtr self, byte* key, byte* value);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_ISteamFriends_GetFriendRichPresence(IntPtr self, ulong id, byte* key);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern void SteamAPI_ISteamFriends_RequestFriendRichPresence(IntPtr self, ulong id);

        private IntPtr _friends;

        // ---- ISteamSocial (friends and rich presence; polled, no callbacks) --------------------------

        bool ISteamSocial.Ready => Ready && _friends != IntPtr.Zero;

        public string LocalName
        {
            get
            {
                if (!_ready || _friends == IntPtr.Zero) return "Player";
                return Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetPersonaName(_friends)) ?? "Player";
            }
        }

        public System.Collections.Generic.IList<ulong> Friends()
        {
            var list = new System.Collections.Generic.List<ulong>();
            if (!_ready || _friends == IntPtr.Zero) return list;
            int n = SteamAPI_ISteamFriends_GetFriendCount(_friends, FriendFlagImmediate);
            for (int i = 0; i < n && i < 2000; i++) list.Add(SteamAPI_ISteamFriends_GetFriendByIndex(_friends, i, FriendFlagImmediate));
            return list;
        }

        public string FriendName(ulong id)
        {
            if (!_ready || _friends == IntPtr.Zero) return "?";
            return Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetFriendPersonaName(_friends, id)) ?? "?";
        }

        public bool FriendInThisGame(ulong id)
        {
            if (!_ready || _friends == IntPtr.Zero) return false;
            byte* info = stackalloc byte[32];
            for (int i = 0; i < 32; i++) info[i] = 0;
            if (SteamAPI_ISteamFriends_GetFriendGamePlayed(_friends, id, info) == 0) return false;
            ulong gameId = *(ulong*)info;
            return (gameId & 0xFFFFFFUL) == ThisGameAppId; // the app id is the low 24 bits of a game id
        }

        public string GetRich(ulong id, string key)
        {
            if (!_ready || _friends == IntPtr.Zero) return "";
            IntPtr k = Marshal.StringToCoTaskMemUTF8(key);
            try { return Marshal.PtrToStringUTF8(SteamAPI_ISteamFriends_GetFriendRichPresence(_friends, id, (byte*)k)) ?? ""; }
            finally { Marshal.FreeCoTaskMem(k); }
        }

        public void SetRich(string key, string value)
        {
            if (!_ready || _friends == IntPtr.Zero) return;
            IntPtr k = Marshal.StringToCoTaskMemUTF8(key);
            IntPtr v = string.IsNullOrEmpty(value) ? IntPtr.Zero : Marshal.StringToCoTaskMemUTF8(value);
            try { SteamAPI_ISteamFriends_SetRichPresence(_friends, (byte*)k, (byte*)v); } // a null value clears the key
            finally { Marshal.FreeCoTaskMem(k); if (v != IntPtr.Zero) Marshal.FreeCoTaskMem(v); }
        }

        public void RequestRich(ulong id)
        {
            if (_ready && _friends != IntPtr.Zero) SteamAPI_ISteamFriends_RequestFriendRichPresence(_friends, id);
        }

        private const string Lib = "steam_api64";
        private const int Channel = 0;
        private const int SendUnreliableNoDelay = 1; // k_EP2PSendUnreliableNoDelay

        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_IsSteamRunning();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamUser_v020();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern IntPtr SteamAPI_SteamNetworking_v006();
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamUser_BLoggedOn(IntPtr self);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern ulong SteamAPI_ISteamUser_GetSteamID(IntPtr self);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_SendP2PPacket(IntPtr self, ulong remote, byte* data, uint size, int sendType, int channel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_IsP2PPacketAvailable(IntPtr self, uint* size, int channel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_ReadP2PPacket(IntPtr self, byte* dest, uint destSize, uint* size, ulong* remote, int channel);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(IntPtr self, ulong remote);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_CloseP2PSessionWithUser(IntPtr self, ulong remote);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_AllowP2PPacketRelay(IntPtr self, byte allow);
        [DllImport(Lib, CallingConvention = CallingConvention.Cdecl)] private static extern byte SteamAPI_ISteamNetworking_GetP2PSessionState(IntPtr self, ulong remote, byte* state);

        private static bool _resolverInstalled;

        private IntPtr _net;
        private bool _ready;
        private bool _broken;
        private long _nextTryMs;
        private readonly byte[] _rx = new byte[2048];

        public ulong LocalId { get; private set; }

        public SteamP2PNative()
        {
            InstallResolver();
        }

        public bool Ready
        {
            get
            {
                if (_ready) return true;
                if (_broken) return false;
                long now = Environment.TickCount64;
                if (now < _nextTryMs) return false;
                _nextTryMs = now + 1000;
                TryInit();
                return _ready;
            }
        }

        private static void InstallResolver()
        {
            if (_resolverInstalled) return;
            _resolverInstalled = true;
            NativeLibrary.SetDllImportResolver(typeof(SteamP2PNative).Assembly, new DllImportResolver(Resolve));
        }

        // A named method instead of a lambda: the delegate's nullable annotations make the compiler want NullableAttribute,
        // which the game's .NET 6 reference assemblies do not provide.
        private static IntPtr Resolve(string name, System.Reflection.Assembly assembly, DllImportSearchPath? searchPath)
        {
            if (name != Lib) return IntPtr.Zero;
            string path = FindDll();
            if (path != null && NativeLibrary.TryLoad(path, out IntPtr handle)) return handle;
            return IntPtr.Zero;
        }

        private static string FindDll()
        {
            string root = BepInEx.Paths.GameRootPath;
            foreach (string dataDir in Directory.GetDirectories(root, "*_Data"))
            {
                string p = Path.Combine(dataDir, "Plugins", "x86_64", "steam_api64.dll");
                if (File.Exists(p)) return p;
            }
            return null;
        }

        private void TryInit()
        {
            try
            {
                if (SteamAPI_IsSteamRunning() == 0) return;
                IntPtr user = SteamAPI_SteamUser_v020();
                if (user == IntPtr.Zero) return;          // the game has not initialised Steam yet
                if (SteamAPI_ISteamUser_BLoggedOn(user) == 0) return;
                IntPtr net = SteamAPI_SteamNetworking_v006();
                if (net == IntPtr.Zero) return;
                ulong id = SteamAPI_ISteamUser_GetSteamID(user);
                if (id == 0) return;

                _net = net;
                _friends = SteamAPI_SteamFriends_v017(); // may be null; then only the P2P part works
                LocalId = id;
                byte relay = SteamAPI_ISteamNetworking_AllowP2PPacketRelay(net, 1);
                _ready = true;
                Plugin.Logger.LogInfo($"co-op: Steam is ready (Steam ID {id}, friends API {(_friends != IntPtr.Zero ? "ok" : "missing")}), AllowP2PPacketRelay -> {relay}");
            }
            catch (DllNotFoundException e)
            {
                _broken = true;
                Plugin.Logger.LogError($"co-op: steam_api64.dll could not be loaded, Steam transport disabled: {e.Message}");
            }
            catch (EntryPointNotFoundException e)
            {
                _broken = true;
                Plugin.Logger.LogError($"co-op: steam_api64.dll lacks a function we need, Steam transport disabled: {e.Message}");
            }
            catch (Exception e)
            {
                _broken = true;
                Plugin.Logger.LogError($"co-op: Steam init failed, Steam transport disabled: {e}");
            }
        }

        public bool Send(ulong peer, byte[] data)
        {
            if (!_ready) return false;
            fixed (byte* p = data)
                return SteamAPI_ISteamNetworking_SendP2PPacket(_net, peer, p, (uint)data.Length, SendUnreliableNoDelay, Channel) != 0;
        }

        public bool TryReceive(out ulong from, out byte[] data)
        {
            from = 0;
            data = null;
            if (!_ready) return false;

            uint size = 0;
            if (SteamAPI_ISteamNetworking_IsP2PPacketAvailable(_net, &size, Channel) == 0) return false;

            uint read = 0;
            ulong remote = 0;
            byte ok;
            fixed (byte* buf = _rx)
                ok = SteamAPI_ISteamNetworking_ReadP2PPacket(_net, buf, (uint)_rx.Length, &read, &remote, Channel);
            if (ok == 0 || read == 0 || read > _rx.Length) return false;

            data = new byte[read];
            Buffer.BlockCopy(_rx, 0, data, 0, (int)read);
            from = remote;
            return true;
        }

        public void Accept(ulong peer)
        {
            if (_ready) SteamAPI_ISteamNetworking_AcceptP2PSessionWithUser(_net, peer);
        }

        public void Close(ulong peer)
        {
            if (_ready) SteamAPI_ISteamNetworking_CloseP2PSessionWithUser(_net, peer);
        }

        public string Describe(ulong peer)
        {
            if (!_ready) return "not-ready";
            byte* st = stackalloc byte[32];
            for (int i = 0; i < 32; i++) st[i] = 0;
            if (SteamAPI_ISteamNetworking_GetP2PSessionState(_net, peer, st) == 0) return "peer=no-session";
            int queued = *(int*)(st + 4);
            return $"peer: active={st[0]} connecting={st[1]} err={st[2]} relay={st[3]} queuedBytes={queued}";
        }
    }
}
