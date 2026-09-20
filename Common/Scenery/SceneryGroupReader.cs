using System.IO;
using Common.Scenery.Data;

namespace Common.Scenery
{
    /// <summary>
    /// Reads the two chunks that make up the real game's named scenery
    /// override-group system (hyperlinked's scenery::group / scenery::
    /// override_info) - siblings of the per-section scenery container
    /// (0x80034100), not children of it. Kept as its own reader rather than
    /// folded into CarbonScenery, since these two chunks are not
    /// per-section/per-game-specific the way CarbonScenery's contents are.
    /// </summary>
    public static class SceneryGroupReader
    {
        // scenery_override_infos (0x00034108) - flat array, 6 bytes each:
        // section_number(u16), instance_number(u16), instance_flags(u16).
        public static SceneryOverrideInfoTable ReadOverrideInfos(BinaryReader br, uint chunkSize)
        {
            var table = new SceneryOverrideInfoTable();
            var count = (int)chunkSize / 6;

            for (var i = 0; i < count; i++)
            {
                table.Overrides.Add(new SceneryOverrideInfo
                {
                    SectionNumber = br.ReadUInt16(),
                    InstanceNumber = br.ReadUInt16(),
                    InstanceFlags = br.ReadUInt16(),
                });
            }

            return table;
        }

        // scenery_groups (0x00034109) - variable-length records, NOT a fixed
        // stride. Each record is scenery::group's real on-disk layout:
        //   0x00 next_/prev_  (2x uint32 - the game's intrusive linked-list
        //                      node pointers, meaningless on disk, same
        //                      "runtime pointer baked into the record"
        //                      pattern already seen elsewhere in this
        //                      project - skipped, not read)
        //   0x08 key                        (uint32 - string hash of the group name)
        //   0x0C group_number               (uint16)
        //   0x0E override_count             (uint16)
        //   0x10 barrier_flag                (byte)
        //   0x11 drive_through_barrier_flag  (byte)
        //   0x12 race_specific_section_number (uint16)
        //   0x14 overrides[override_count]  (uint16 each - indices into the
        //                                    SceneryOverrideInfoTable from
        //                                    the sibling 0x00034108 chunk)
        // then padding to the NEXT multiple of 4 bytes (per the real
        // loader's `current += 4 - (current & 3)` - this always advances,
        // even when already aligned, unlike a typical round-up-if-needed
        // align step. Worth confirming against a real file before trusting
        // this literally, since it's an unusual enough pattern to double
        // check.
        public static SceneryGroupTable ReadGroups(BinaryReader br, uint chunkSize)
        {
            var table = new SceneryGroupTable();
            var endPos = br.BaseStream.Position + chunkSize;

            while (br.BaseStream.Position < endPos)
            {
                br.BaseStream.Seek(8, SeekOrigin.Current); // next_/prev_, meaningless on disk

                var key = br.ReadUInt32();
                var groupNumber = br.ReadUInt16();
                var overrideCount = br.ReadUInt16();
                var barrierFlag = br.ReadByte();
                var driveThroughBarrierFlag = br.ReadByte();
                var raceSpecificSectionNumber = br.ReadUInt16();

                var overrideIndices = new ushort[overrideCount];
                for (var i = 0; i < overrideCount; i++)
                    overrideIndices[i] = br.ReadUInt16();

                table.Groups.Add(new SceneryGroup
                {
                    Key = key,
                    GroupNumber = groupNumber,
                    BarrierFlag = barrierFlag,
                    DriveThroughBarrierFlag = driveThroughBarrierFlag,
                    RaceSpecificSectionNumber = raceSpecificSectionNumber,
                    OverrideIndices = overrideIndices,
                });

                var pos = br.BaseStream.Position;
                var padding = 4 - (int)(pos % 4);
                br.BaseStream.Seek(padding, SeekOrigin.Current);
            }

            return table;
        }
    }
}
