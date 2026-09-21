using System;
using System.Collections.Generic;
using System.Numerics;

namespace Common.WorldAnim.Data
{
    /// <summary>
    /// One parsed world_anim rt_node (0x00037250). Layout confirmed via real
    /// hex from a live scan (see project notes) - not the original
    /// hyperlinked-derived guess, which was off by the leading 4-byte
    /// sentinel every record actually starts with.
    /// </summary>
    public class WorldAnimNode
    {
        public uint Key { get; set; }
        public sbyte ParentIndex { get; set; }
        public byte TimeScale { get; set; } // raw byte - byte->float formula NOT yet confirmed, see TimeScaleFactor
        public bool IsLibraryAnim { get; set; }
        public bool UseLibraryAnim { get; set; }
        public bool UseParentAnim { get; set; }
        public uint[] SolidKeys { get; set; } = new uint[3];
        public uint SmackableKey { get; set; }
        public uint SceneryGuid { get; set; }
        public short[] RotationSpeed { get; set; } = new short[3]; // degrees/sec per local axis, per-axis - confirmed real via multiple samples
        public ushort SectionNumber { get; set; }
        public uint KeyFrameCount { get; set; }
        public bool IsLooping { get; set; }
        public bool IsPingPong { get; set; }
        public short InitialAngle { get; set; }
        public Matrix4x4 Transform { get; set; }

        /// <summary>Only populated when KeyFrameCount &gt; 0 - authored/sampled frames (e.g. section 2600).</summary>
        public List<Matrix4x4> Frames { get; set; }

        /// <summary>
        /// TODO: Carbon's on-disk TimeScale byte->float normalization is NOT
        /// confirmed. Every real sample seen so far is exactly 16 (0x10);
        /// byte/16.0 (making 16 the "1.0x" default) is a clean hypothesis
        /// but unproven - no sample with a different value has been found
        /// yet. Isolated here on purpose so it's a one-line fix once proven.
        /// </summary>
        public float TimeScaleFactor => TimeScale / 16.0f;

        private const float BaseFrameRate = 30.0f; // confirmed from MW source (WorldAnimCtrl)

        /// <summary>
        /// Returns real authored frames if present, otherwise bakes synthetic
        /// keyframes for a procedural constant-rotation node (section 2400
        /// pattern: no keyframes, angle = initial_angle + rotation_speed*time
        /// around whichever local axis has a nonzero rotation_speed).
        ///
        /// ASSUMPTIONS not yet visually verified in Blender:
        ///  - rotation composes as (local spin) * (static transform), i.e.
        ///    the object spins around its own local axis before being placed
        ///    - matches row-vector convention already used elsewhere in this
        ///    codebase (v' = v * M), but the resulting spin DIRECTION/handedness
        ///    has not been checked against real gameplay footage.
        ///  - if more than one axis has a nonzero rotation_speed, only the
        ///    first nonzero one found (X, then Y, then Z) is used - every
        ///    real sample seen so far has exactly one nonzero axis, so this
        ///    hasn't actually been exercised.
        /// </summary>
        private IReadOnlyList<Matrix4x4> _diagnosedFrames;

        public IReadOnlyList<Matrix4x4> GetOrBakeFrames(out float framesPerSecond)
        {
            framesPerSecond = BaseFrameRate;

            if (KeyFrameCount > 0 && Frames is { Count: > 0 })
            {
                // DIAGNOSTIC ONLY - not a fix. We don't yet know whether the
                // scale instability seen on export is (a) a decomposition-
                // branch artifact on otherwise-constant scale (safe to
                // normalize away) or (b) genuine intentional scale change
                // over the clip (which a naive "hold frame 0's scale"
                // normalization would silently destroy). Logging every raw
                // frame's decomposed scale/determinant so that decision is
                // made from real numbers, not a guess. Frames are returned
                // UNMODIFIED - no normalization applied yet.
                _diagnosedFrames ??= LogAndReturnRawFrames(Frames, Key, SceneryGuid);
                return _diagnosedFrames;
            }

            var axisIndex = Array.FindIndex(RotationSpeed, s => s != 0);
            if (axisIndex < 0)
                return Array.Empty<Matrix4x4>(); // static node - nothing to animate

            var degreesPerSecond = RotationSpeed[axisIndex] * TimeScaleFactor;
            if (degreesPerSecond == 0) return Array.Empty<Matrix4x4>();

            var axis = axisIndex switch
            {
                0 => Vector3.UnitX,
                1 => Vector3.UnitY,
                _ => Vector3.UnitZ,
            };

            var periodSeconds = 360.0 / Math.Abs(degreesPerSecond);
            var frameCount = Math.Max(2, (int)Math.Round(periodSeconds * BaseFrameRate));

            var baked = new List<Matrix4x4>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var timeSeconds = i / BaseFrameRate;
                var angleDegrees = InitialAngle + degreesPerSecond * timeSeconds;
                var angleRadians = angleDegrees * (MathF.PI / 180f);
                var spin = Matrix4x4.CreateFromAxisAngle(axis, angleRadians);
                baked.Add(spin * Transform);
            }

            return baked;
        }

        private static IReadOnlyList<Matrix4x4> LogAndReturnRawFrames(IReadOnlyList<Matrix4x4> source, uint key, uint sceneryGuid)
        {
            for (var i = 0; i < source.Count; i++)
            {
                var frame = source[i];
                var det = frame.GetDeterminant();
                var decomposed = Matrix4x4.Decompose(frame, out var scale, out _, out _);
                Console.Error.WriteLine(
                    $"[WorldAnimNode] key=0x{key:X8} guid=0x{sceneryGuid:X8} frame {i}/{source.Count}: " +
                    $"det={det:F6} decompose_ok={decomposed} " +
                    $"scale=({scale.X:F6},{scale.Y:F6},{scale.Z:F6})");
            }
            return source;
        }
    }

    public class WorldAnimBank : BasicResource
    {
        public List<WorldAnimNode> Nodes { get; } = new();

    }
}
