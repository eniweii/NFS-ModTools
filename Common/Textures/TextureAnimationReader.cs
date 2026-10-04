using System.Collections.Generic;
using System.IO;
using System.Text;
using Common.Textures.Data;

namespace Common.Textures
{
    /// <summary>
    /// Reads the texture_pack_anim chunk family (Carbon layout, verified on two real samples).
    ///
    /// texture_pack_anim_header (0x33312001), 0x2C byte payload:
    ///   +0x08 name[16], +0x18 key u32, +0x1C frame_count u8, +0x1D fps u8, +0x1E time_base u8
    /// texture_pack_anim_frames (0x33312002) follows the header with no gap.
    ///   Payload = frame_count * 12 bytes. Each entry: key u32 (real texture hash) + 8 runtime-pointer bytes.
    ///
    /// A header whose frames chunk is missing, or whose length is not frame_count * 12, is
    /// treated as a false positive and dropped.
    /// </summary>
    public static class TextureAnimationReader
    {
        public const uint PackChunkId = 0xB3312000;
        public const uint HeaderChunkId = 0x33312001;
        public const uint FramesChunkId = 0x33312002;

        private const int HeaderMinSize = 0x20;
        private const int FrameEntrySize = 12;

        /// <summary>
        /// Walks a container payload (stream positioned at the first child chunk).
        /// Recurses into nested containers. Leaves the stream at the end of the payload.
        /// </summary>
        public static void ReadContainer(BinaryReader br, uint length, List<TextureAnimation> into)
        {
            var end = br.BaseStream.Position + length;
            TextureAnimation pending = null;

            while (br.BaseStream.Position + 8 <= end)
            {
                var id = br.ReadUInt32();
                var size = br.ReadUInt32();
                var chunkEnd = br.BaseStream.Position + size;
                if (chunkEnd > end) break; // misaligned - stop instead of reading garbage

                if ((id & 0x80000000) != 0)
                {
                    pending = null;
                    ReadContainer(br, size, into);
                }
                else if (id == HeaderChunkId && size >= HeaderMinSize)
                {
                    pending = ReadHeader(br);
                }
                else if (id == FramesChunkId && pending != null)
                {
                    if (size == pending.FrameCount * FrameEntrySize)
                    {
                        for (var i = 0; i < pending.FrameCount; i++)
                        {
                            pending.FrameHashes.Add(br.ReadUInt32());
                            br.BaseStream.Position += FrameEntrySize - 4;
                        }

                        into.Add(pending);
                    }

                    pending = null;
                }
                else
                {
                    pending = null;
                }

                br.BaseStream.Position = chunkEnd;
            }

            br.BaseStream.Position = end;
        }

        private static TextureAnimation ReadHeader(BinaryReader br)
        {
            br.BaseStream.Position += 8; // reserved, zero in both samples
            var nameBytes = br.ReadBytes(16);
            var nul = System.Array.IndexOf(nameBytes, (byte)0);
            var name = Encoding.Latin1.GetString(nameBytes, 0, nul < 0 ? nameBytes.Length : nul);

            return new TextureAnimation
            {
                Name = name,
                Key = br.ReadUInt32(),
                FrameCount = br.ReadByte(),
                Fps = br.ReadByte(),
                TimeBase = (TextureAnimationTimeBase)br.ReadByte(),
            };
        }
    }
}
