using System.Collections.Generic;

namespace Common.Textures.Data
{
    public enum TextureAnimationTimeBase : byte { World = 0, Real = 1 }

    /// <summary>
    /// One texture frame-swap "playlist" (texture_pack_anim_header + texture_pack_anim_frames).
    /// Frames are plain texture hashes that resolve against the normal texture list.
    /// Playback (hyperlinked): frame = (int)(Fps * time) % FrameCount.
    /// </summary>
    public class TextureAnimation
    {
        /// <summary>Fixed 16-byte buffer on disk, so long names are truncated. Use Key to identify.</summary>
        public string Name { get; set; }
        public uint Key { get; set; }
        public byte FrameCount { get; set; }
        public byte Fps { get; set; }
        public TextureAnimationTimeBase TimeBase { get; set; }
        public List<uint> FrameHashes { get; set; } = new();
    }

    /// <summary>Resource holder for animations read from a top-level texture_pack_anim_pack chunk.</summary>
    public class TextureAnimationBank : BasicResource
    {
        public List<TextureAnimation> Animations { get; } = new();
    }
}
