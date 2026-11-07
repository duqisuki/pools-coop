using System;
using System.Collections.Generic;

namespace PoolsCoop.Net
{
    /// <summary>The few Steam friends calls the invite flow needs. Native in the plugin, a fake in the tests.</summary>
    public interface ISteamSocial
    {
        bool Ready { get; }
        ulong LocalId { get; }
        string LocalName { get; }
        IList<ulong> Friends();
        string FriendName(ulong id);

        /// <summary>True if this friend is running this very game right now.</summary>
        bool FriendInThisGame(ulong id);

        /// <summary>Reads a rich-presence key of a friend (or of ourselves).</summary>
        string GetRich(ulong id, string key);

        /// <summary>Sets one of our own rich-presence keys; an empty value clears it.</summary>
        void SetRich(string key, string value);

        void RequestRich(ulong id);
    }

    public sealed class HostedGame
    {
        public ulong HostId;
        public string Name;
    }

    /// <summary>
    /// Invites without callbacks and without typing codes. Steam shows a player's "rich presence" to their friends, so it is the
    /// message channel: a host publishes <c>poolscoop_host</c> (its own Steam ID); friends running the game see it in
    /// <see cref="HostedGames"/> and can join with one button; a friend who joins publishes <c>poolscoop_join</c> (the host's ID),
    /// and the host, polling its friends, sees who is asking and lets exactly those friends in.
    /// A claim only counts if the ID in it is the one the friend is advertising for: nobody can send us to a third person's game.
    /// </summary>
    public sealed class CoopLobby
    {
        public const string KeyHost = "poolscoop_host";
        public const string KeyJoin = "poolscoop_join";
        public const string KeyStatus = "status";
        public const long PollEveryMs = 2000;

        public enum LobbyState { Idle, Hosting, Joining }

        private readonly ISteamSocial _social;
        private readonly IClock _clock;
        private long _lastPollMs = -PollEveryMs;
        private readonly HashSet<ulong> _askingToJoin = new HashSet<ulong>();
        private readonly List<HostedGame> _hosted = new List<HostedGame>();

        public CoopLobby(ISteamSocial social, IClock clock)
        {
            _social = social;
            _clock = clock;
        }

        public LobbyState State { get; private set; }
        public ulong JoiningHost { get; private set; }

        /// <summary>Friends currently hosting, as of the last poll. Only filled while we are idle.</summary>
        public IReadOnlyList<HostedGame> HostedGames => _hosted;

        /// <summary>Host side: a friend asked to join us. Fires once per friend until they stop asking.</summary>
        public event Action<ulong> JoinerAppeared;

        /// <summary>Host side: a friend who asked to join stopped asking (left, or joined someone else).</summary>
        public event Action<ulong> JoinerGone;

        public void StartHosting()
        {
            if (!_social.Ready) return;
            State = LobbyState.Hosting;
            JoiningHost = 0;
            _hosted.Clear();
            _social.SetRich(KeyJoin, "");
            _social.SetRich(KeyHost, _social.LocalId.ToString());
            _social.SetRich(KeyStatus, "Co-op: hosting");
            _lastPollMs = -PollEveryMs;
        }

        public void StartJoining(ulong hostId)
        {
            if (!_social.Ready || hostId == 0) return;
            State = LobbyState.Joining;
            JoiningHost = hostId;
            _hosted.Clear();
            _social.SetRich(KeyHost, "");
            _social.SetRich(KeyJoin, hostId.ToString());
            _social.SetRich(KeyStatus, "Co-op: joining a friend");
        }

        public void Stop()
        {
            if (_social.Ready)
            {
                _social.SetRich(KeyHost, "");
                _social.SetRich(KeyJoin, "");
                _social.SetRich(KeyStatus, "");
            }
            foreach (ulong id in new List<ulong>(_askingToJoin)) JoinerGone?.Invoke(id);
            _askingToJoin.Clear();
            State = LobbyState.Idle;
            JoiningHost = 0;
            _lastPollMs = -PollEveryMs;
        }

        /// <summary>Call every frame; it polls Steam at most every two seconds.</summary>
        public void Update()
        {
            if (!_social.Ready) return;
            long now = _clock.NowMs;
            if (now - _lastPollMs < PollEveryMs) return;
            _lastPollMs = now;

            if (State == LobbyState.Idle) PollHosts();
            else if (State == LobbyState.Hosting) PollJoiners();
        }

        private static bool TryId(string text, out ulong id)
        {
            id = 0;
            return !string.IsNullOrEmpty(text) && ulong.TryParse(text, out id) && id != 0;
        }

        private void PollHosts()
        {
            _hosted.Clear();
            foreach (ulong f in _social.Friends())
            {
                if (f == _social.LocalId || !_social.FriendInThisGame(f)) continue;
                _social.RequestRich(f);
                // The ID inside the claim must be the friend's own: otherwise anybody could list somebody else as a host.
                if (TryId(_social.GetRich(f, KeyHost), out ulong claimed) && claimed == f)
                    _hosted.Add(new HostedGame { HostId = f, Name = _social.FriendName(f) });
            }
        }

        private void PollJoiners()
        {
            var nowAsking = new HashSet<ulong>();
            foreach (ulong f in _social.Friends())
            {
                if (f == _social.LocalId || !_social.FriendInThisGame(f)) continue;
                _social.RequestRich(f);
                if (TryId(_social.GetRich(f, KeyJoin), out ulong target) && target == _social.LocalId) nowAsking.Add(f);
            }
            foreach (ulong f in nowAsking)
                if (_askingToJoin.Add(f)) JoinerAppeared?.Invoke(f);
            foreach (ulong f in new List<ulong>(_askingToJoin))
                if (!nowAsking.Contains(f)) { _askingToJoin.Remove(f); JoinerGone?.Invoke(f); }
        }
    }
}
