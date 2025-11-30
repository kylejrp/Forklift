using System.Diagnostics;
using System.Runtime.CompilerServices;

namespace Forklift.Core
{
    public enum Color : byte { White = 0, Black = 1 }

    public static class ColorEx
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Color Flip(this Color c) => (Color)((byte)c ^ 1);

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWhite(this Color c) => c == Color.White;
    }

    /// <summary>0..7 file index (a..h). Throws in debug if out of range.</summary>
    public readonly record struct FileIndex(byte Value)
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public FileIndex(int v) : this((byte)v)
        {
            Debug.Assert((uint)v <= 7u, "FileIndex value out of range");
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator int(FileIndex f) => f.Value;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static explicit operator FileIndex(int v) => new FileIndex(v);
    }
}
