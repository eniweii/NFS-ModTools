using System;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using System.Runtime.InteropServices;
using Common.WorldAnim.Data;

namespace Common.WorldAnim
{
    /// <summary>
    /// Reader for Carbon's world_anim chunk family (scenery motion animation).
    ///
    /// Important:
    ///   world_anim_frames is NOT necessarily located at chunk data + 0.
    ///   Hyperlinked's native loader uses:
    ///
    ///       block->aligned_data(0x10u)
    ///
    ///   Therefore ReadFrames() aligns the absolute stream position to a
    ///   16-byte boundary before reading the 64-byte Matrix4x4 frames.
    ///
    /// Chunk IDs:
    ///   world_anim_header = 0x00037220
    ///   world_anim_frames = 0x00037240
    ///   world_anim_rtnode = 0x00037250
    ///   world_anim_counts = 0x00037260
    ///   world_anim_endptr = 0x00037270
    /// </summary>
    public class WorldAnimReader
    {
        public const uint HeaderChunkId = 0x00037220;
        public const uint FramesChunkId = 0x00037240;
        public const uint RtNodeChunkId = 0x00037250;
        public const uint CountsChunkId = 0x00037260;
        public const uint EndPtrChunkId = 0x00037270;

        private const uint Sentinel = 0x11111111;
        private const int Matrix4x4ByteSize = 64;
        private const int FrameAlignment = 0x10;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        private struct RtNodeInternal
        {
            // 16 floats, row-major. Carbon/Hyperlinked uses the same
            // 4x4 matrix representation consumed here by System.Numerics.
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
            public ushort Unknown0x6E;
            public byte UseParentFrames;
            public byte Pad1;
            public short Pad2;
        }

        private readonly WorldAnimBank _bank = new();
        private WorldAnimNode _pendingFramesNode;

        public WorldAnimBank Finish() => _bank;

        public void ReadHeader(BinaryReader br, uint chunkSize)
        {
            // Header fields are not required for per-node extraction yet.
            // Consume nothing; ChunkManager seeks to the chunk end after
            // this callback.
        }

        public void ReadCounts(BinaryReader br, uint chunkSize)
        {
            // Confirmed layout: sentinel(4) + node_count(4).
            if (chunkSize < 8)
                return;

            var sentinel = br.ReadUInt32();
            var nodeCount = br.ReadUInt32();

            if (sentinel != Sentinel)
            {
                Console.Error.WriteLine(
                    $"[WorldAnimReader] world_anim_counts has unexpected sentinel " +
                    $"0x{sentinel:X8}; expected 0x{Sentinel:X8}.");
            }

            // Kept as a future cross-check when tree/bank grouping is rebuilt.
            _ = nodeCount;
        }

        public void ReadNode(BinaryReader br, uint chunkSize)
        {
            if (chunkSize < 4)
            {
                _pendingFramesNode = null;
                return;
            }

            var sentinel = br.ReadUInt32();

            if (sentinel != Sentinel ||
                chunkSize < 4 + Marshal.SizeOf<RtNodeInternal>())
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

                SolidKeys = new[]
                {
                    raw.SolidKey0,
                    raw.SolidKey1,
                    raw.SolidKey2
                },

                SmackableKey = raw.SmackableKey,
                SceneryGuid = raw.SceneryGuid,

                RotationSpeed = new[]
                {
                    raw.RotationSpeedX,
                    raw.RotationSpeedY,
                    raw.RotationSpeedZ
                },

                SectionNumber = raw.SectionNumber,
                KeyFrameCount = raw.KeyFrameCount,

                IsPingPong = (raw.Bits & 0x40000000) != 0,
                IsLooping = (raw.Bits & 0x80000000) != 0,

                InitialAngle = raw.InitialAngle
            };

            _bank.Nodes.Add(node);

            // Hyperlinked only associates a following frames chunk when
            // this node actually owns authored frames. Library/parent
            // animations borrow frames elsewhere.
            _pendingFramesNode =
                node.KeyFrameCount > 0 &&
                !node.UseLibraryAnim &&
                !node.UseParentAnim
                    ? node
                    : null;
        }

        public void ReadFrames(BinaryReader br, uint chunkSize)
        {
            if (_pendingFramesNode == null)
                return;

            var node = _pendingFramesNode;
            _pendingFramesNode = null;

            var expectedCount = node.KeyFrameCount;

            if (expectedCount == 0)
                return;

            if (expectedCount > int.MaxValue)
            {
                Console.Error.WriteLine(
                    $"[WorldAnimReader] world_anim_frames for node key=0x{node.Key:X8}: " +
                    $"KeyFrameCount={expectedCount} exceeds supported list size. " +
                    "Leaving node static.");
                return;
            }

            // Hyperlinked's loader obtains the frame pointer with
            // block->aligned_data(0x10u). The alignment is relative to the
            // absolute address of block->data(), i.e. the current file/stream
            // position when this callback starts.
            var payloadStart = br.BaseStream.Position;
            var alignmentMask = FrameAlignment - 1;
            var padding = (FrameAlignment - ((int)(payloadStart & alignmentMask))) & alignmentMask;

            var expectedFrameBytes = checked((ulong)expectedCount * Matrix4x4ByteSize);
            var expectedChunkBytes = checked((ulong)padding + expectedFrameBytes);

            if (expectedChunkBytes != chunkSize)
            {
                Console.Error.WriteLine(
                    $"[WorldAnimReader] world_anim_frames for node key=0x{node.Key:X8} " +
                    $"(SceneryGuid=0x{node.SceneryGuid:X8}): " +
                    $"chunkSize={chunkSize}, absolutePayload=0x{payloadStart:X}, " +
                    $"alignmentPadding={padding}, KeyFrameCount={expectedCount}, " +
                    $"frameBytes={expectedFrameBytes}, expectedChunkBytes={expectedChunkBytes}. " +
                    "Layout does not match the Hyperlinked 16-byte-aligned frame-array rule; " +
                    "leaving this node static.");
                return;
            }

            if (padding > 0)
            {
                var paddingBytes = br.ReadBytes(padding);

                if (paddingBytes.Length != padding)
                {
                    Console.Error.WriteLine(
                        $"[WorldAnimReader] Failed to read {padding} bytes of frame alignment " +
                        $"padding for node key=0x{node.Key:X8}; leaving node static.");
                    return;
                }

                Console.Error.WriteLine(
                    $"[WorldAnimReader] world_anim_frames key=0x{node.Key:X8}: " +
                    $"skipped {padding} alignment byte(s) before frame array.");
            }

            var frames = new List<Matrix4x4>((int)expectedCount);

            for (var i = 0; i < (int)expectedCount; i++)
            {
                // Matrix4x4 is 16 float32 values = 64 bytes.
                frames.Add(new Matrix4x4(
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle(),
                    br.ReadSingle(), br.ReadSingle(), br.ReadSingle(), br.ReadSingle()));
            }

            node.Frames = frames;
        }

        public void ReadEndPtr(BinaryReader br, uint chunkSize)
        {
            // Observed in the real chunk stream, but its payload semantics
            // are not yet decoded. ChunkManager handles repositioning.
        }
    }
}
