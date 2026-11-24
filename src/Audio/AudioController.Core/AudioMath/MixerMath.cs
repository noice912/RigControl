namespace AudioController.Core.AudioMath;

/// <summary>
/// The one and only place where soundboard audio meets microphone audio.
/// The microphone path is NEVER modified when no soundboard audio is present:
/// pass-through is bit-exact. Clipping protection (clamp to ±1.0) is applied
/// only at this final summing stage, and only to samples that were mixed.
/// </summary>
public static class MixerMath
{
    /// <summary>Sums <paramref name="board"/> into <paramref name="mic"/> in place, clamping to ±1.0.</summary>
    public static void MixInto(float[] mic, int offset, int count, float[] board, int boardOffset)
    {
        ArgumentNullException.ThrowIfNull(mic);
        ArgumentNullException.ThrowIfNull(board);

        for (int i = 0; i < count; i++)
        {
            float sum = mic[offset + i] + board[boardOffset + i];
            if (sum > 1f) sum = 1f;
            else if (sum < -1f) sum = -1f;
            mic[offset + i] = sum;
        }
    }
}
