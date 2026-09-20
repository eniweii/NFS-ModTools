using System.Collections.Generic;

namespace Common.Scenery.Data
{
    // scenery::override_info (hyperlinked) - 6-byte flat record, one per
    // instance that a group can override. section_number+instance_number
    // together identify which SceneryInstance this points at.
    public class SceneryOverrideInfo
    {
        public ushort SectionNumber { get; set; }
        public ushort InstanceNumber { get; set; }
        public ushort InstanceFlags { get; set; }
    }

    public class SceneryOverrideInfoTable : BasicResource
    {
        public List<SceneryOverrideInfo> Overrides { get; set; } = new();
    }

    // scenery::group (hyperlinked) - a named (string-hash keyed) collection
    // of override_info entries. This is the real game's own toggle-group
    // system (scenery::group::enable(key)/disable(key)) - e.g. "SCENERY_GROUP_DOOR".
    public class SceneryGroup
    {
        public uint Key { get; set; }
        public ushort GroupNumber { get; set; }
        public byte BarrierFlag { get; set; }
        public byte DriveThroughBarrierFlag { get; set; }
        public ushort RaceSpecificSectionNumber { get; set; }

        // Indices into the SceneryOverrideInfoTable read from the sibling
        // scenery_override_infos chunk (0x00034108).
        public ushort[] OverrideIndices { get; set; }
    }

    public class SceneryGroupTable : BasicResource
    {
        public List<SceneryGroup> Groups { get; set; } = new();
    }
}
