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
        public bool UseParentFrames { get; set; }

        /// <summary>
        /// Index of this node's tree within the flat Nodes list (i.e. the
        /// list index of tree element 0), so ParentIndex - which the real
        /// engine resolves as tree->nodes[ParentIndex], local to this node's
        /// own tree - can be turned into an absolute index. Set by
        /// WorldAnimReader while reading; not populated from the file itself.
        /// </summary>
        public int TreeStartIndex { get; set; }

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
        /// Real hold time at each swing extreme before reversing, so the
        /// direction change doesn't judder. Confirmed to be real (matches
        /// CWorldAnimCtrl's MasterDelayTime/LocalDelayTime pause-before-loop
        /// behavior, dbalatoni13/nfsmw WorldAnimCtrl.cpp) but the exact
        /// duration is a visual approximation from real gameplay footage,
        /// not a decoded value - isolated here so it's a one-line tune.
        /// </summary>
        private const float PauseSeconds = 0.1f;

        /// <summary>
        /// Returns real authored frames if present, otherwise bakes synthetic
        /// keyframes for a procedural node (section 2400 pattern: no
        /// keyframes, driven by RotationSpeed/InitialAngle around whichever
        /// local axis has a nonzero RotationSpeed).
        ///
        /// Confirmed via real gameplay footage (searchlight, CarLotFlags)
        /// cross-checked against real data:
        ///  - InitialAngle == 0: unbounded continuous rotation (0->360 wrap).
        ///  - InitialAngle != 0: bounded swing 0 -> +angle -> 0 -> -angle ->
        ///    0 -> ..., eased like a sine wave (this engine's own native
        ///    idiom for sway - see rain.cpp's CreateWindRotMatrix,
        ///    sway = sin(...) * swayMax - though not the identical code
        ///    path), with a PauseSeconds hold at each +-angle extreme.
        ///
        /// Still an assumption, not yet visually verified in Blender:
        ///  - rotation composes as (local spin) * (static transform), i.e.
        ///    the object spins around its own local axis before being placed
        ///    - matches row-vector convention already used elsewhere in this
        ///    codebase (v' = v * M), but the resulting spin DIRECTION/handedness
        ///    has not been checked against real gameplay footage.
        ///  - if more than one axis has a nonzero RotationSpeed, only the
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

            var degreesPerSecond = Math.Abs(RotationSpeed[axisIndex] * TimeScaleFactor);
            if (degreesPerSecond == 0) return Array.Empty<Matrix4x4>();

            var axis = axisIndex switch
            {
                0 => Vector3.UnitX,
                1 => Vector3.UnitY,
                _ => Vector3.UnitZ,
            };

            return InitialAngle == 0
                ? BakeFullRotation(axis, degreesPerSecond, framesPerSecond)
                : BakeBoundedSwing(axis, degreesPerSecond, Math.Abs((float)InitialAngle), framesPerSecond);
        }

        private List<Matrix4x4> BakeFullRotation(Vector3 axis, float degreesPerSecond, float framesPerSecond)
        {
            var periodSeconds = 360.0 / degreesPerSecond;
            var frameCount = Math.Max(2, (int)Math.Round(periodSeconds * framesPerSecond));

            var baked = new List<Matrix4x4>(frameCount);
            for (var i = 0; i < frameCount; i++)
            {
                var timeSeconds = i / framesPerSecond;
                var angleDegrees = degreesPerSecond * timeSeconds;
                var angleRadians = angleDegrees * (MathF.PI / 180f);
                var spin = Matrix4x4.CreateFromAxisAngle(axis, angleRadians);
                baked.Add(spin * Transform);
            }

            return baked;
        }

        private List<Matrix4x4> BakeBoundedSwing(Vector3 axis, float degreesPerSecond, float amplitudeDegrees, float framesPerSecond)
        {
            // RotationSpeed is treated as the swing's peak (zero-crossing)
            // angular velocity of a sine wave: amplitude * omega = degreesPerSecond.
            var omega = degreesPerSecond / amplitudeDegrees; // "radians"/sec in this angle-domain sine
            var quarterPeriodSeconds = (MathF.PI / 2f) / omega;
            var totalCycleSeconds = 4f * quarterPeriodSeconds + 2f * PauseSeconds;

            var frameCount = Math.Max(4, (int)Math.Round(totalCycleSeconds * framesPerSecond));
            var baked = new List<Matrix4x4>(frameCount);

            for (var i = 0; i < frameCount; i++)
            {
                var realTime = i / framesPerSecond;
                var phaseTime = RealTimeToPhaseTime(realTime, quarterPeriodSeconds, PauseSeconds);
                var angleDegrees = amplitudeDegrees * MathF.Sin(omega * phaseTime);
                var angleRadians = angleDegrees * (MathF.PI / 180f);
                var spin = Matrix4x4.CreateFromAxisAngle(axis, angleRadians);
                baked.Add(spin * Transform);
            }

            return baked;
        }

        /// <summary>
        /// Maps elapsed real time within one swing cycle to the equivalent
        /// "phase" time fed into sin(omega * phase) - identical to real time
        /// except phase is frozen for PauseSeconds right after each +-angle
        /// extreme (end of the 1st and 3rd quarter-period), producing the
        /// hold-then-resume behavior confirmed from real footage.
        /// </summary>
        private static float RealTimeToPhaseTime(float t, float quarterPeriod, float pause)
        {
            var t1 = quarterPeriod;           // reach +angle
            var t2 = t1 + pause;              // pause at +angle ends
            var t3 = t2 + quarterPeriod;      // back through 0
            var t4 = t3 + quarterPeriod;      // reach -angle
            var t5 = t4 + pause;              // pause at -angle ends

            if (t <= t1) return t;
            if (t <= t2) return quarterPeriod;
            if (t <= t3) return quarterPeriod + (t - t2);
            if (t <= t4) return 2 * quarterPeriod + (t - t3);
            if (t <= t5) return 3 * quarterPeriod;
            return 3 * quarterPeriod + (t - t5);
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
