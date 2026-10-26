using System;
using UnityEngine;

namespace MultiplayerLobbyFixes
{
    /// <summary>
    /// The pacer every query the mod causes goes through: a number of queries a second, paid out as a number of
    /// queries a frame.
    ///
    /// Each frame works out what its own time is worth, the frame time multiplied by the rate, and takes that many
    /// out of whatever is waiting. The frame time is `Time.smoothDeltaTime`, which is the engine's own average: it
    /// follows a steady frame rate exactly and lags a single long frame, so a hitch is paid for over the frames
    /// after it instead of in one burst. The engine's own `Time.maximumDeltaTime` caps it, so however far that
    /// average is dragged by a long stall, one frame never spends more than the game itself is willing to call a
    /// frame.
    ///
    /// What is held between frames is one frame's worth plus the request in hand, so nothing is banked: a frame
    /// that asks nothing cannot pay itself back as a pile on a later one. The request in hand is what keeps the
    /// fractional part of a worth smaller than one query, and lets an ask that costs more than a frame is worth,
    /// a rules query and a ping at a rate below twice the frame rate, go out once the rate has saved up for it.
    ///
    /// Both flows spend the one rate: the game's queries, which the queue holds, charge one each, and the mod's own
    /// asks charge what they cost, a rules query and a ping a server.
    /// </summary>
    internal sealed class Rate
    {
        private readonly Func<float> _perSecond;

        private float _worth;

        private float _left;

        private int _frame = -1;

        internal Rate(Func<float> perSecond)
        {
            _perSecond = perSecond;
        }

        /// <summary>Whether this ask is paid for out of the frame's own worth. A yes charges it against that.</summary>
        internal bool Take(int queries = 1)
        {
            if (Time.frameCount != _frame)
            {
                _frame = Time.frameCount;
                _worth = Mathf.Min(Time.smoothDeltaTime, Time.maximumDeltaTime) * Math.Max(1f, _perSecond());
                _left = Math.Min(_left + _worth, _worth + queries);
            }

            if (_left < queries)
            {
                return false;
            }

            _left -= queries;
            return true;
        }
    }
}
