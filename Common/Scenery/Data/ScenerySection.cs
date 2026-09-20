using System;
using System.Collections.Generic;
using System.Numerics;

namespace Common.Scenery.Data
{
    // Real, complete enum from hyperlinked's assets/scenery.hpp (Carbon's own
    // decompiled source) - cross-confirmed field-for-field against this
    // project's own SceneryInstanceInternal struct. Bits 0-15 are live/
    // overridable at runtime (scenery::override_info can change them after
    // load); bits 16-31 are static, baked in at load, and never touched by
    // the game's own override system - confirmed via override_info::
    // assign_overrides, which always masks to preserve the high 16 bits.
    [Flags]
    public enum SceneryInstanceFlags : uint
    {
        None = 0,

        // --- live / overridable (bits 0-15) ---
        ExcludeSplitScreen = 1u << 0,
        ExcludeMainView = 1u << 1,
        ExcludeRacing = 1u << 2,
        ExcludeDisableRendering = 1u << 3,
        ExcludeGroupDisable = 1u << 4,
        ExcludeFreeroam = 1u << 5,
        IncludeRearView = 1u << 6,
        IncludeReflection = 1u << 7,
        EnvmapShadow = 1u << 8,
        ChoppedRoadway = 1u << 9,
        IdentityMatrix = 1u << 10,
        ArtworkFlipped = 1u << 11, // the real mirror-flip bit
        Reflection = 1u << 12,
        EnvironmentMap = 1u << 13,
        Swayable = 1u << 14,
        EnableWind = 1u << 15,

        // --- static, baked at load (bits 16-31) ---
        AlwaysFacing = 1u << 16,
        DontReceiveShadows = 1u << 17,
        LowPlatformOnly = 1u << 18,
        HighPlatformOnly = 1u << 19,
        CastShadowVolume = 1u << 20,
        CastShadowMap = 1u << 21,
        InvertedMatrix = 1u << 22,
        FlipOnBackwardsTrack = 1u << 23,
        ReflectInOcean = 1u << 24,
        VisibleFurther = 1u << 25,
        CastShadowMapMesh = 1u << 26,
        IncludeReflectionNg = 1u << 27,
        AuxLighting = 1u << 28,
        PcPlatform = 1u << 29,
        Collidable = 1u << 30,
        Lightmapped = 1u << 31,
    }

    public static class SceneryInstanceFlagsMasks
    {
        // Only these bits can ever be changed by scenery::override_info after
        // load - matches the exact mask used in the real assign_overrides.
        public const uint Live = 0x0000FFFF;
        public const uint Static = 0xFFFF0000;
    }

    public class SceneryInfo
    {
        public string Name { get; set; }
        public uint SolidKey { get; set; }
        public bool IsDeinstanced { get; set; }

        // Per-model (not per-instance) flags - real field name/meaning not
        // yet decoded, only the per-instance enum above has been. Kept raw
        // until that's done.
        public uint Flags { get; set; }
    }

    public class SceneryInstance
    {
        public int InfoIndex { get; set; }
        public Matrix4x4 Transform { get; set; }
        public SceneryInstanceFlags Flags { get; set; }
        public uint SceneryGuid { get; set; }
    }
    
    public class ScenerySection : BasicResource
    {
        public int SectionNumber { get; set; }
        public List<SceneryInfo> Infos { get; set; } = new List<SceneryInfo>();
        public List<SceneryInstance> Instances { get; set; } = new List<SceneryInstance>();
    }
}