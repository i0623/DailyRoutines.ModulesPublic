namespace DailyRoutines.ModulesPublic;

internal partial class AutoReuseEmote
{
    internal sealed class EmotePlayback
    (
        ushort   emoteID,
        ushort   previousEmoteID,
        ushort[] timelineIDs
    )
    {
        private long? lastPlayingAt;

        internal bool IsInterrupted(ushort currentEmoteID) =>
            currentEmoteID != 0               &&
            currentEmoteID != emoteID         &&
            currentEmoteID != previousEmoteID;

        internal bool ObserveStart(ushort currentEmoteID, ReadOnlySpan<ushort> activeTimelines, long now)
        {
            if (lastPlayingAt.HasValue)
                return true;

            if (!IsPlaying(currentEmoteID, activeTimelines))
                return false;

            lastPlayingAt = now;
            return true;
        }

        internal bool HasFinished(ushort currentEmoteID, ReadOnlySpan<ushort> activeTimelines, long now)
        {
            if (!lastPlayingAt.HasValue)
                return false;

            if (IsPlaying(currentEmoteID, activeTimelines))
            {
                lastPlayingAt = now;
                return false;
            }

            return now - lastPlayingAt.Value >= EMOTE_END_SETTLE_MS;
        }

        private bool IsPlaying(ushort currentEmoteID, ReadOnlySpan<ushort> activeTimelines)
        {
            if (currentEmoteID == emoteID)
                return true;

            foreach (var timelineID in activeTimelines)
            {
                if (timelineID != 0 && timelineIDs.AsSpan().Contains(timelineID))
                    return true;
            }

            return false;
        }

        #region 常量

        private const int EMOTE_END_SETTLE_MS = 150;

        #endregion
    }
}
