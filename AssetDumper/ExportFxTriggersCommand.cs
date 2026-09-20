using System;
using System.Collections.Generic;
using System.IO;
using CommandLine;
using Serilog;

namespace AssetDumper;

// NOTE ON CONFIDENCE: this is meaningfully less certain than the rest of
// this codebase's readers, though less so than it used to be.
//   1. The layout below replaces the original guess derived from
//      EmitterLibraryHeader::GetLibrary in the MW decompiled source
//      (dbalatoni13/nfsmw) - that method is marked "// UNSOLVED" by whoever
//      decompiled it, and the nested "0x50 library + mNumTriggers x 0x30
//      trigger" shape it implied did NOT match real bytes (see point 2).
//   2. BCHUNK_SPEED_EMITTER_LIBRARY (0x0003BC00) IS confirmed present in a
//      real file: one instance (chunk at file offset 0x195A0B20 in the
//      user's STREAML5RA.BUN) has been hand-decoded byte-by-byte, and the
//      flat "0x18-byte header + record_count x 0x50-byte record" shape
//      documented on ParseEmitterLibraryChunk below matches that instance's
//      bytes exactly, including a plausible embedded world position. Only
//      that ONE of the file's 211 real instances has been checked this way
//      - the other 210 are still unverified, and the middle 0x30 bytes of
//      each record (where radius/probability/state/flags might live) are
//      still undecoded.
// Treat every printed record as a hypothesis to sanity-check by eye
// (do the positions look like real in-level coordinates, etc.) rather
// than something to trust blindly - the same way every "candidate" struct
// elsewhere in this project needed hex verification before being trusted.
[Verb("scan-fx-triggers",
    HelpText = "Scan a raw stream/bundle file for WorldFXTrigger emitter-trigger " +
                "placements and write their positions to a text file. Does NOT do " +
                "a full scenery re-export or load the file's other content into " +
                "memory - reads the chunk envelope only and skips (Seek, not Read) " +
                "over anything that isn't the target chunk, so this stays cheap " +
                "regardless of how large the file is.")]
public class ExportFxTriggersCommand : BaseCommand
{
    [Option('f', "file", Required = true, HelpText = "Path to the raw stream/bundle file to scan")]
    public string File { get; set; }

    [Option('o', "output", Required = true, HelpText = "Path to write the resulting tab-separated report to")]
    public string Output { get; set; }

    // BCHUNK_SPEED_EMITTER_LIBRARY, per nfsmw's SpeedChunks.hpp
    // (0x0003BC00 + DATA_BCHUNK, and DATA_BCHUNK == 0)
    private const uint EmitterLibraryChunkId = 0x0003BC00;
    private const uint ContainerFlag = 0x80000000;

    private int _chunksFound;
    private int _triggersFound;
    private int _triggersRejected;

    // Real, compiled-in name<->hash pairs for every named particle effect in
    // MW's shipped code (Generated/AttribSys/Classes/emittergroup_hash.h) -
    // NOT reverse-engineered or guessed, these are the actual hash constants
    // the game itself uses. GroupKey is embedded directly in the on-disk
    // EmitterLibrary record (see ParseEmitterLibraryChunk below), so no
    // AttribSys/runtime loading is needed to resolve a name - it's a pure
    // static lookup. A handful of these (car/dust/water/sparks/etc) look
    // like broader category hashes rather than a specific named effect -
    // included anyway since a match is still more useful than a raw hex
    // value either way.
    // NOTE: this list is MW's - if run against a Carbon file, any GroupKey
    // not in this table prints as UNKNOWN_0x... rather than a wrong guess.
    // Carbon likely has its own additional/renamed effects this list won't
    // cover.
    private static readonly Dictionary<uint, string> KnownEffectNames = new()
    {
        { 0xa13753eb, "car" },
        { 0xfda45513, "carsurface" },
        { 0x490792b5, "debris" },
        { 0xeec2271a, "default" },
        { 0x7cd7ef85, "destruction" },
        { 0x33c586e2, "dust" },
        { 0x3c04df64, "environmental" },
        { 0xcb223b96, "explosions" },
        { 0x5e2fe5bc, "fire" },
        { 0xb585f0e2, "fxcar_cop_damage1" },
        { 0x3aeb075d, "fxcar_cop_death1" },
        { 0x080847fc, "fxcar_coplightblue" },
        { 0xb647956d, "fxcar_coplightred" },
        { 0xcd495af7, "fxcar_coplightwhite" },
        { 0x9ee4cac4, "fxcar_dusttrail1" },
        { 0xc9fbd50f, "fxcar_engineblow1" },
        { 0xb25d04fb, "fxcar_exhaust_bmw" },
        { 0x59038c67, "fxcar_exhaust_bmw2" },
        { 0x5fcba64b, "fxcar_exhaust_drip" },
        { 0xf74dfaaf, "fxcar_impactdebrisl" },
        { 0xf991f8e8, "fxcar_impactl" },
        { 0xf033a657, "fxcar_impactpavement" },
        { 0x6b7916bd, "fxcar_nos" },
        { 0x67f294de, "fxcar_tireblow" },
        { 0x3e898d27, "fxcs_sc_metal" },
        { 0x84800b86, "FxCS_Sc_Stone" },
        { 0x32cb8388, "FxCS_Sc_Wood" },
        { 0xfd94eabe, "fxdust_lg_billow1" },
        { 0xc3c0fa78, "fxdust_lg_fall" },
        { 0x43bb74f8, "fxdust_med_billow1" },
        { 0x34c14de7, "fxdust_med_exup" },
        { 0xd48711db, "fxdust_med_fall" },
        { 0x45f3b7ed, "fxenv_bird" },
        { 0x39ede226, "fxenv_birdblack01" },
        { 0x4d9e7f58, "fxenv_blackbird02" },
        { 0xfd888a4b, "fxenv_chimney1" },
        { 0x08259ccf, "fxenv_dustmotes1" },
        { 0xc8a23629, "fxenv_fog_fe1" },
        { 0x48d22c27, "fxenv_fog_fe2" },
        { 0x378d447e, "fxenv_fog1" },
        { 0x0b9204ce, "fxenv_fog1thick" },
        { 0xf8170eed, "fxenv_fog2" },
        { 0x8010a402, "fxenv_fountain1" },
        { 0x85676c22, "fxenv_fountain2" },
        { 0x67306f57, "fxenv_fountain3" },
        { 0x328cd168, "fxenv_leaffall_hvy" },
        { 0x96ac4f78, "fxenv_leaves1" },
        { 0x303dc26f, "fxenv_ripple1" },
        { 0x476537c3, "fxenv_ripple2" },
        { 0x4b54ad54, "fxenv_small_steam1" },
        { 0x31fb2ab6, "fxenv_small_steam2" },
        { 0xaa7dab77, "fxenv_small_steam3" },
        { 0x2f23057a, "fxenv_smokestack" },
        { 0xf5ed0a9b, "fxenv_smokestack_blk" },
        { 0xfd8995c1, "fxenv_smokestack_brn" },
        { 0xfe17b5f9, "fxenv_smokestack_long" },
        { 0x56447450, "fxex_carexplode_sm1" },
        { 0xb6f25977, "fxex_gasstation" },
        { 0xed95a4ae, "fxex_large1" },
        { 0x4b8cef14, "fxex_large2" },
        { 0x8fed3b3e, "fxfire_lg_area1" },
        { 0x9cd7d94e, "fxfire_sm1" },
        { 0xd36ad240, "fxfire_trail1" },
        { 0xb9fb31b1, "fxgame_flare_green" },
        { 0x310805cf, "fxgame_flare_red" },
        { 0x6699d23d, "fxgame_icongrp_chop" },
        { 0x3ccb52c5, "fxgame_icongrp_circuit" },
        { 0xc0087531, "fxgame_icongrp_drag" },
        { 0xf9f71422, "fxgame_icongrp_hidecar" },
        { 0xb8e326bf, "fxgame_icongrp_lapk" },
        { 0xbe1ef064, "fxgame_icongrp_lot" },
        { 0x7546c031, "fxgame_icongrp_pursuit" },
        { 0x8a2709be, "fxgame_icongrp_rivalr" },
        { 0x6d018122, "fxgame_icongrp_safe" },
        { 0x60bb33ec, "fxgame_icongrp_speedt" },
        { 0xe1eb3b0f, "fxgame_icongrp_sprint" },
        { 0x5a4699bf, "fxgame_icongrp_tollb" },
        { 0x102985e8, "fxmis_coins1" },
        { 0x53fdef32, "fxmis_dustpuff" },
        { 0xb14a8cf9, "fxmis_glass1" },
        { 0x186e0544, "fxmis_grasshit" },
        { 0xa6d64ac8, "fxmis_hitdust1" },
        { 0x4a707043, "fxmis_leaffall1" },
        { 0xaffbbf1d, "fxmis_leafhit" },
        { 0xfe2ed9d8, "fxmis_paper1" },
        { 0x1c27e839, "fxmis_wooddust1" },
        { 0x040d2469, "fxnis_extradust1" },
        { 0x53b9b550, "fxnis_leafblast1" },
        { 0x4e4e9bbe, "fxnis_leafblast2" },
        { 0x5cfeb5aa, "fxnis_leafblast3" },
        { 0x715d7ea4, "fxnis_leafblast4" },
        { 0x0a2097e1, "fxnis_steamjet" },
        { 0xb7a0ad8a, "fxsmk_md_trail" },
        { 0x2db37a81, "fxsmk_md_trail02" },
        { 0xb6db8816, "fxsprk_lg_dir" },
        { 0x8566e9e3, "fxsprk_md_dir" },
        { 0xeabb4fe1, "fxsprk_md_trail" },
        { 0x95cde6cb, "fxtd_dr_asphalt_leaves" },
        { 0x0eac606a, "fxtd_dr_asphalt_noleaves" },
        { 0x98c0d7ac, "fxtd_dr_asphalt_wet" },
        { 0x1129219d, "fxtd_dr_cobble" },
        { 0x21a3994d, "fxtd_dr_cobble_wet" },
        { 0xcc2d92ba, "fxtd_dr_dirt" },
        { 0x66f77142, "fxtd_dr_grass" },
        { 0x9e89df3e, "fxtd_dr_grass_wet" },
        { 0x6624ab85, "fxtd_dr_sand" },
        { 0xb8366e92, "fxtd_dr_sand_wet" },
        { 0xeddf159f, "fxtd_fly_asphalt" },
        { 0x87d2afb4, "fxtd_fly_dirt" },
        { 0x3fc1d096, "fxtd_hit_grass" },
        { 0xd6ba7bbf, "fxtd_hit_sand" },
        { 0xeaa2e866, "fxtd_sk_asphalt" },
        { 0xd2f8d5ba, "fxtd_sk_asphalt_no_leaves" },
        { 0x45861103, "fxtd_sk_cobble" },
        { 0xe3ffd367, "fxtd_sk_sand" },
        { 0x7790c105, "fxtd_sl_asphalt" },
        { 0x1c9bde03, "fxtd_sl_grass" },
        { 0x437a9a0b, "fxwtr_fountain1" },
        { 0x0582cc9e, "fxwtr_waterbarrel_L" },
        { 0x898ee059, "fxwtr_waterbarrel_sm" },
        { 0x5cea9d46, "gameplay" },
        { 0x8e6342c8, "nis" },
        { 0x0b11b7b2, "smoke" },
        { 0x2cad8553, "sparks" },
        { 0x0aee9ee6, "terraindriving" },
        { 0x5a2e0437, "water" },
        { 0x7cce05d6, "xeci_car" },
        { 0xc14b9283, "xecs_solid_wall" },
        { 0x9576caca, "env_lavasparks1" },
        { 0xa9ff07db, "env_lavasplatter1" },
        { 0x239f3ccf, "fxenv_chimney2" },
        { 0x15006165, "fxenv_fog2thick" },
        { 0x36fb97ef, "fxenv_fountain4" },
        { 0xa5a82fec, "fxenv_fountain5" },
        { 0x0aee710e, "fxenv_fountain6" },
        { 0x31e6b159, "fxenv_fountain7" },
        { 0x18333526, "fxenv_lava1" },
        { 0x0978da3c, "fxenv_moths1" },
        { 0x73ef880e, "fxenv_smokestack_refinery" },
        { 0x39c8da21, "fxenv_torchfire1" },
        { 0xefa8fadf, "fxenv_torchfire2" },
        { 0x7c9a286d, "fxenv_torchfire3" },
        { 0xe016f4cb, "fxenv_torchfire4" },
    };

    public override int Execute()
    {
        Log.Information("Scanning {File} for EmitterLibrary (0x{ChunkId:X}) chunks",
            File, EmitterLibraryChunkId);

        using var stream = System.IO.File.OpenRead(File);
        using var reader = new BinaryReader(stream);
        using var writer = new StreamWriter(Output);

        writer.WriteLine(string.Join('\t',
            "chunk_offset", "section_number", "chunk_section_ref", "record_section_ref",
            "record_index", "group_key", "group_name", "world_x", "world_y", "world_z"));

        ScanChunks(reader, stream.Length, writer);

        Log.Information(
            "Done. {NumChunks} EmitterLibrary chunk(s) found, {NumTriggers} record(s) written, " +
            "{NumRejected} rejected by sanity checks (see warnings above)",
            _chunksFound, _triggersFound, _triggersRejected);

        if (_chunksFound == 0)
        {
            Log.Warning(
                "No EmitterLibrary chunks found anywhere in this file. This either means " +
                "this particular file doesn't contain any, or the chunk ID or container " +
                "assumptions above are wrong for this game/file version - the layout below " +
                "them has been hand-verified against one real instance, but not yet re-run " +
                "here end-to-end.");
        }

        return 0;
    }

    /// <summary>
    /// Walks the generic chunk envelope (uint32 type, uint32 length, payload -
    /// same format as every other .BUN-style file in this engine) from the
    /// current stream position up to endPos. Recurses into container chunks
    /// (top bit of Type set). For anything that isn't the target chunk, seeks
    /// past its payload WITHOUT reading it into memory - this is the actual
    /// difference from ChunkManager, which buffers every chunk's raw bytes
    /// into a List&lt;Chunk&gt; regardless of whether anything ever uses it,
    /// which is what makes it impractical on a multi-gigabyte file for this
    /// purpose.
    /// </summary>
    private void ScanChunks(BinaryReader reader, long endPos, StreamWriter writer)
    {
        var stream = reader.BaseStream;

        while (stream.Position + 8 <= endPos)
        {
            var chunkOffset = stream.Position;
            uint type = reader.ReadUInt32();
            uint length = reader.ReadUInt32();
            long payloadStart = stream.Position;
            long payloadEnd = payloadStart + length;

            if (length > int.MaxValue || payloadEnd > endPos)
            {
                // Truncated or misaligned - stop rather than keep walking
                // garbage. Matches nfs_region_common.walk_chunks's own
                // defensive stance on this same situation.
                Log.Warning(
                    "Stopping scan at offset 0x{Offset:X}: chunk declares length {Length} " +
                    "which runs past the current container's end - truncated file or " +
                    "misparsed alignment upstream of this point.", chunkOffset, length);
                return;
            }

            bool isContainer = (type & ContainerFlag) != 0;

            if (type == EmitterLibraryChunkId)
            {
                _chunksFound++;
                ParseEmitterLibraryChunk(reader, chunkOffset, payloadStart, length, writer);
                stream.Seek(payloadEnd, SeekOrigin.Begin);
            }
            else if (isContainer)
            {
                ScanChunks(reader, payloadEnd, writer);
                stream.Seek(payloadEnd, SeekOrigin.Begin);
            }
            else
            {
                stream.Seek(payloadEnd, SeekOrigin.Begin);
            }
        }
    }

    /// <summary>
    /// Parses one EmitterLibraryHeader chunk's payload. Layout below is
    /// confirmed against real bytes (chunk at file offset 0x195A0B20 in the
    /// user's STREAML5RA.BUN, one of 211 real 0x0003BC00 instances) - not
    /// the old GetLibrary/UNSOLVED-derived guess anymore, though only this
    /// one instance has been hand-verified so far.
    ///
    /// 0x18-byte header:
    ///   endian_swapped(4), version(4) - both 0x11111111 in real data, not 0
    ///   library_count(4)   - CONFIRMED always 0 on disk, not a usable count
    ///   section_number(4)  - low 16 bits used, high 16 bits padding
    ///   record_count(4)    - the real count to loop; old code never read
    ///                        this field at all, so it always saw
    ///                        library_count=0 and silently produced nothing
    ///   section_ref(4)     - matches section_number in the one instance
    ///                        checked so far
    /// then record_count flat 0x50 (80-byte) records, NOT the old nested
    /// "0x50 library + mNumTriggers x 0x30 trigger" shape:
    ///   group_key(4), pad(4), record_section_ref(4), pad(4)  - each record
    ///     carries its own key (a real hash-shaped value here, unlike the
    ///     header's record_count field which is far too small to be one)
    ///   unknown_block(0x30) - mostly 0x00000000/0x3F800000 (1.0f), matrix-
    ///     shaped but not yet decoded; radius/probability/state/flags may
    ///     live in here somewhere, unconfirmed - kept raw rather than
    ///     guessed at
    ///   world_x, world_y, world_z(4 each) - realistic in-level coordinates
    ///     in the verified record
    ///   pad/w(4) - observed as 1.0f
    /// </summary>
    private void ParseEmitterLibraryChunk(BinaryReader reader, long chunkOffset,
        long payloadStart, uint payloadLength, StreamWriter writer)
    {
        var stream = reader.BaseStream;
        stream.Seek(payloadStart, SeekOrigin.Begin);
        long payloadEnd = payloadStart + payloadLength;

        if (payloadLength < 0x18)
        {
            Log.Warning("EmitterLibrary chunk at 0x{Offset:X} is only {Length} bytes - " +
                        "too small to even hold the 0x18-byte header, skipping.",
                chunkOffset, payloadLength);
            return;
        }

        int endianSwapped = reader.ReadInt32();
        int version = reader.ReadInt32();
        int legacyLibraryCount = reader.ReadInt32(); // confirmed always 0 - logged only, not used
        int sectionNumber = reader.ReadInt32();
        int recordCount = reader.ReadInt32();
        int chunkSectionRef = reader.ReadInt32();

        if (endianSwapped != unchecked((int)0x11111111))
        {
            Log.Warning("EmitterLibrary chunk at 0x{Offset:X} has EndianSwapped=0x{Value:X8} " +
                        "(expected the 0x11111111 sentinel seen in the verified real record) - " +
                        "this chunk may be a different shape or a big-endian console build.",
                chunkOffset, endianSwapped);
        }

        if (legacyLibraryCount != 0)
        {
            Log.Information("EmitterLibrary chunk at 0x{Offset:X}: library_count field is " +
                             "{Value}, not the usually-0 value seen so far - worth a closer look, " +
                             "not necessarily wrong.", chunkOffset, legacyLibraryCount);
        }

        if (recordCount < 0 || recordCount > 10000)
        {
            Log.Warning("EmitterLibrary chunk at 0x{Offset:X} declares record_count={Value} - " +
                        "rejecting as implausible rather than trying to walk it (wrong chunk ID " +
                        "match, or the layout assumption above is wrong for this file).",
                chunkOffset, recordCount);
            return;
        }

        for (int recIndex = 0; recIndex < recordCount; recIndex++)
        {
            if (stream.Position + 0x50 > payloadEnd)
            {
                Log.Warning("EmitterLibrary chunk at 0x{Offset:X}: ran out of room reading " +
                            "record {Index} of {Total} - stopping this chunk here.",
                    chunkOffset, recIndex, recordCount);
                return;
            }

            uint groupKey = reader.ReadUInt32();
            reader.ReadUInt32(); // pad
            uint recordSectionRef = reader.ReadUInt32();
            reader.ReadUInt32(); // pad
            reader.ReadBytes(0x30); // unknown_block - not needed for a position dump, skipped
                                    // rather than mis-parsed (available here if ever decoded)
            float wx = reader.ReadSingle();
            float wy = reader.ReadSingle();
            float wz = reader.ReadSingle();
            reader.ReadSingle(); // observed as 1.0f - w component or padding

            bool positionLooksSane = Math.Abs(wx) < 1_000_000 && Math.Abs(wy) < 1_000_000
                                                               && Math.Abs(wz) < 1_000_000
                                      && !float.IsNaN(wx) && !float.IsNaN(wy) && !float.IsNaN(wz);

            if (!positionLooksSane)
            {
                _triggersRejected++;
                Log.Warning(
                    "Rejecting record {RecIndex} at 0x{Offset:X}: pos=({X}, {Y}, {Z}) - doesn't " +
                    "look like real data. This is the strongest signal that the layout assumption " +
                    "is wrong if it happens on every record.",
                    recIndex, chunkOffset, wx, wy, wz);
                continue;
            }

            _triggersFound++;
            string groupName = KnownEffectNames.TryGetValue(groupKey, out var name)
                ? name : $"UNKNOWN_0x{groupKey:X8}";
            writer.WriteLine(string.Join('\t',
                $"0x{chunkOffset:X}", sectionNumber, chunkSectionRef, recordSectionRef, recIndex,
                $"0x{groupKey:X8}", groupName, wx, wy, wz));
        }
    }
}
