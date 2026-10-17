using System;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// The pacer every query the mod causes goes through: a number of queries a second, paid out as a number of
    /// queries a frame.
    ///
    /// Each frame works out what its own time is worth, the average frame time multiplied by the rate, and takes
    /// that many out of whatever is waiting. Working from the average rather than from the frame's own length
    /// spreads one long frame over the window instead of paying for it in full, and a frame rate that stays low
    /// keeps the average up so the pacer pays for the longer frames.
    ///
    /// What is held between frames is one frame's worth plus the request in hand, so nothing is banked: a stale
    /// gap cannot pay itself back as a pile on the frame that ends it. The request in hand is what keeps the
    /// fractional part of a worth smaller than one query, and lets an ask that costs more than a frame is worth,
    /// a rules query and a ping at a rate below twice the frame rate, go out once the rate has saved up for it.
    ///
    /// Both flows spend the one rate: the game's queries, which the queue holds, charge one each, and the mod's own
    /// asks charge what they cost, a rules query and a ping a server.
    /// </summary>
    internal sealed class Rate
    {
        private readonly Func<float> _perSecond;

        private readonly Func<int> _window;

        private float _frameSeconds;

        private float _worth;

        private float _left;

        private float _at;

        private bool _started;

        private bool _averaged;

        internal Rate(Func<float> perSecond, Func<int> window)
        {
            _perSecond = perSecond;
            _window = window;
        }

        /// <summary>Whether this ask is paid for out of the frame's own worth. A yes charges it against that.</summary>
        internal bool Take(float now, int queries = 1)
        {
            if (!_started || now != _at)
            {
                if (_started)
                {
                    Average(now - _at);
                }

                _started = true;
                _at = now;

                _worth = _frameSeconds * Math.Max(1f, _perSecond());
                _left = Math.Min(_left + _worth, _worth + queries);
            }

            if (_left < queries)
            {
                return false;
            }

            _left -= queries;
            return true;
        }

        /// <summary>The frames the worth is worked out from: the first gap, then a window's worth of each.</summary>
        private void Average(float gap)
        {
            if (!_averaged)
            {
                _frameSeconds = gap;
                _averaged = true;
                return;
            }

            _frameSeconds += (gap - _frameSeconds) / Math.Max(1, _window());
        }
    }
}
