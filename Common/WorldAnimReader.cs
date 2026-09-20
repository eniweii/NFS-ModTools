using System;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Common.WorldAnim.Data;

namespace Common.WorldAnim
{
    /// <summary>
    /// Reader for Carbon's world_anim chunk family (scenery motion animation -
    /// NOT particle effects/EmitterLibrary, NOT texture UV/frame animation).
    ///
    /// Chunk IDs:
    ///   world_anim_header = 0x00037220  (fields UNVERIFIED - see ReadHeader)
    ///   world_anim_frames = 0x00037240  (UNVERIFIED - no real sample with
    ///                                     KeyFrameCount > 0 has been dumped
    ///                                     yet; every real record seen so far
    ///                                     has KeyFrameCount == 0)
    ///   world_anim_rtnode = 0x00037250  (CONFIRMED via real hex, multiple
    ///                                     independent samples)
    ///   world_anim_counts = 0x00037260  (CONFIRMED via real hex)
    ///   world_anim_endptr = 0x00037270  (bytes seen but role/meaning not
    ///                                     decoded - captured raw, unused)
    ///
    /// Deliberately does NOT try to reconstruct header/bank/tree grouping.
    /// Every real rt_node is self-contained (own SceneryGuid, own
    /// SectionNumber), which is all that's needed to attach animation to the
    /// right scenery instance - so every node found anywhere in the file is
    /// collected into one flat list, keyed later by SceneryGuid by whoever
    /// consumes WorldAnimBank.
    /// </summary>
    public class WorldAnimReader
    {
        public const uint HeaderChunkId = 0x00037220;
        public const uint FramesChunkId = 0x00037240;
        public const uint RtNodeChunkId = 0x00037250;
        public const uint CountsChunkId = 0x00037260;
        public const uint EndPtrChunkId = 0x00037270;

        private const uint Sentinel = 0x11111111;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RtNodeInternal
        {
            // 16 floats, row-major, confirmed real: row3 (M41-M44) is
            // translation - matches System.Numerics.Matrix4x4's own
            // row-vector convention already used elsewhere in this codebase.
            public float M11, M12, M13, M14;
            public float M21, M22, M23, M24;
            public float M31, M32, M33, M34;
            public float M41, M42, M43, M44;

            public uint Key;
            public sbyte ParentIndex;
            public byte TimeScale;
            public byte Flags;
            public byte Pad0;
            public uint SolidKey0, SolidKey1, SolidKey2;
            public uint SmackableKey;
            public uint SceneryGuid;
            public short RotationSpeedX, RotationSpeedY, RotationSpeedZ;
            public ushort SectionNumber;
            public uint KeyFrameCount;
            public uint Bits;
            public short InitialAngle;
            public ushort Unknown0x72; // real field, meaning not decoded
            public byte UseParentFrames;
            public byte Pad1;
            public short Pad2;
        }

        private readonly WorldAnimBank _bank = new();
        private WorldAnimNode _pendingFramesNode;

        public WorldAnimBank Finish() => _bank;

        public void ReadHeader(BinaryReader br, uint chunkSize)
        {
            // UNVERIFIED - no real header hex has been dumped/checked in this
            // project yet, unlike every other chunk type here. Not needed for
            // per-node extraction (see class remarks), so left unparsed
            // rather than trusting unverified offsets. Just consumed/ignored;
            // ChunkManager repositions correctly via the chunk's own declared
            // length regardless of what (if anything) is read here.
        }

        public void ReadCounts(BinaryReader br, uint chunkSize)
        {
            // Confirmed real layout: sentinel(4) + node_count(4) = 8 bytes.
            if (chunkSize < 8) return;
            var sentinel = br.ReadUInt32();
            var nodeCount = br.ReadUInt32();
            if (sentinel != Sentinel)
            {
                // Not fatal - just means this candidate may not be a real
                // world_anim_counts chunk (same false-positive risk as any
                // brute-force-discovered chunk ID elsewhere in this project).
            }
            // nodeCount is only useful as a cross-check once bank/tree
            // grouping is reconstructed - not used for anything yet.
            _ = nodeCount;
        }

        public void ReadNode(BinaryReader br, uint chunkSize)
        {
            if (chunkSize < 4) return;
            var sentinel = br.ReadUInt32();
            if (sentinel != Sentinel || chunkSize < 4 + Marshal.SizeOf<RtNodeInternal>())
            {
                _pendingFramesNode = null;
                return;
            }

            var raw = BinaryUtil.ReadStruct<RtNodeInternal>(br);

            var node = new WorldAnimNode
            {
                Transform = new Matrix4x4(
                    raw.M11, raw.M12, raw.M13, raw.M14,
                    raw.M21, raw.M22, raw.M23, raw.M24,
                    raw.M31, raw.M32, raw.M33, raw.M34,
                    raw.M41, raw.M42, raw.M43, raw.M44),
                Key = raw.Key,
                ParentIndex = raw.ParentIndex,
                TimeScale = raw.TimeScale,
                IsLibraryAnim = (raw.Flags & 0x01) != 0,
                UseLibraryAnim = (raw.Flags & 0x02) != 0,
                UseParentAnim = (raw.Flags & 0x04) != 0,
                SolidKeys = new[] { raw.SolidKey0, raw.SolidKey1, raw.SolidKey2 },
                SmackableKey = raw.SmackableKey,
                SceneryGuid = raw.SceneryGuid,
                RotationSpeed = new[] { raw.RotationSpeedX, raw.RotationSpeedY, raw.RotationSpeedZ },
                SectionNumber = raw.SectionNumber,
                KeyFrameCount = raw.KeyFrameCount,
                IsPingPong = (raw.Bits & 0x40000000) != 0, // bit 30
                IsLooping = (raw.Bits & 0x80000000) != 0,  // bit 31
                InitialAngle = raw.InitialAngle,
            };

            _bank.Nodes.Add(node);

            // Per the confirmed real chunk sequence (rtnode immediately
            // followed by its own frames chunk when it has any), only track
            // this node as "expecting frames" when it actually claims some
            // and isn't just borrowing another node's animation.
            _pendingFramesNode = (node.KeyFrameCount > 0 && !node.UseLibraryAnim && !node.UseParentAnim)
                ? node
                : null;
        }

        public void ReadFrames(BinaryReader br, uint chunkSize)
        {
            // UNVERIFIED - no real sample with KeyFrameCount > 0 has been
            // seen yet in this project. Implemented per the doc's proposed
            // shape (contiguous 64-byte float4x4 matrices) as the best
            // available guess; treat every value here as unconfirmed until
            // checked against a real frames chunk.
            if (_pendingFramesNode == null) return;

            var expectedCount = _pendingFramesNode.KeyFrameCount;
            var available = chunkSize / 64;
            var count = (int)Math.Min(expectedCount, available);

            var frames = new System.Collections.Generic.List<Matrix4x4>((int)count);
            for (var i = 0; i < count; i++)
            {
                var m = new float[16];
                for (var j = 0; j < 16; j++) m[j] = br.ReadSingle();
                frames.Add(new Matrix4x4(
                    m[0], m[1], m[2], m[3],
                    m[4], m[5], m[6], m[7],
                    m[8], m[9], m[10], m[11],
                    m[12], m[13], m[14], m[15]));
            }

            _pendingFramesNode.Frames = frames;
            _pendingFramesNode = null;
        }

        public void ReadEndPtr(BinaryReader br, uint chunkSize)
        {
            // Bytes observed in real hex but role/meaning not decoded -
            // nothing needed from it for per-node extraction. Left
            // unconsumed; ChunkManager repositions correctly regardless.
        }
    }
}
