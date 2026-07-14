namespace Jampanion.Core.Generation;

internal static class DeterministicNoise
{
    public static double Unit(int seed, params int[] values)
    {
        unchecked
        {
            uint hash = (uint)seed + 0x9E3779B9u;
            foreach (var value in values)
            {
                hash ^= (uint)value + 0x9E3779B9u + (hash << 6) + (hash >> 2);
            }

            hash ^= hash >> 16;
            hash *= 0x7FEB352Du;
            hash ^= hash >> 15;
            hash *= 0x846CA68Bu;
            hash ^= hash >> 16;
            return hash / (double)uint.MaxValue;
        }
    }
}
