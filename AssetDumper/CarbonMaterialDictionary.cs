using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Common.Geometry.Data;
using Common.Textures.Data;

namespace AssetDumper;

/// <summary>
/// Material dictionary for Carbon/World09 (replaces the old per-solid carbon_materials.jsonl).
///
/// One entry per material key (the export name the DAE uses, so it matches the Blender material name).
/// Each entry lists the distinct "variants" seen for that key: the slot sets (diffuse, normal,
/// specular, height, opacity) plus EffectId, Flags and SortKey.
///
/// Detail merge (non-animated variants only):
///   The variant with the most mapped slots is "recommended". Every less detailed variant whose
///   slots all match the slots of a more detailed variant (same EffectId) gets "replaceWith" = that
///   variant, so Blender can give the material the detailed texture set from the start.
///   A variant that maps a different texture in the same slot is not a subset. It stays "distinct".
///
/// Animated variants (UV scroll or frame swap) never take part in the detail merge.
///   Each solid keeps its own starting variant. One merge exists for frame swap:
///   every frame material of an animation (ANIM002, ANIM003, ...) gets "mergeInto" = the variant
///   with the lowest frame (anim001 when it exists). Variants merge when EffectId and all slots
///   match once each frame hash is replaced by the start frame. Flags and SortKey may differ
///   and are listed in "differsFromTarget". This cuts the material count.
///   The dictionary only describes the merge. The exported files stay unchanged.
///
/// Slots that cannot be right are dropped before the variants are built (diffuse is always kept):
///   - no texture was found for the slot hash
///   - the texture name role suffix contradicts the slot (_D, _N, _S = diffuse, normal, specular),
///     for example ARC_CONCRETE_CLEAN_04_D in a normal slot.
/// A variant that only differed by such a slot then becomes a subset of the main variant, so it
/// is simply replaced and raises no conflict. Dropped slots are listed in "droppedSlots".
/// A scroll with speed 0 and 0 (smooth or snap) is ignored: such a texture is not animated.
/// Scroll decode (hyperlinked effect.cpp / textures.cpp):
///   speed = raw / 4096, timeStep = raw / 256 (snap only),
///   offset = raw / -1024 and scale = raw / 256 (offset_scale only).
/// Frame swap (hyperlinked textures.cpp): frame = (int)(fps * time) % frameCount.
/// </summary>
public static class CarbonMaterialDictionary
{
    private const int MaxSolidsListed = 50;
    private static readonly string[] SlotNames = { "diffuse", "normal", "specular", "height", "opacity" };

    // Expected name role suffix per slot (null = not checked).
    private static readonly char?[] ExpectedSuffix = { 'D', 'N', 'S', null, null };

    private sealed record DroppedSlot(string Slot, uint Hash, string Name, string Reason);

    /// <summary>The role suffix of a texture name (..._D, ..._N, ..._S), or null.</summary>
    private static char? RoleSuffix(string name)
    {
        if (name is not { Length: > 2 } || name[^2] != '_') return null;
        var c = char.ToUpperInvariant(name[^1]);
        return c is 'D' or 'N' or 'S' ? c : null;
    }

    private class Variant
    {
        public uint EffectId, Flags, SortKey;
        public uint?[] Slots = new uint?[5];
        public HashSet<string> Solids = new(StringComparer.Ordinal);
        public HashSet<DroppedSlot> Dropped = new();
        public int Score => Slots.Count(s => s is > 0);

        // Filled by Analyze()
        public int Id;
        public bool HasScroll, HasFrameSwap;
        public bool Animated => HasScroll || HasFrameSwap;
        public int? ReplaceWith;
        public bool IsRoot;
        public string MergeMaterial;
        public int? MergeVariant;
        public List<string> Differs;
    }

    private class MaterialRecord
    {
        public string EffectName;
        public List<Variant> Variants = new();
    }

    private static readonly SortedDictionary<string, MaterialRecord> Materials = new(StringComparer.Ordinal);
    private static readonly SortedDictionary<uint, TextureRecord> Textures = new();
    private static readonly Dictionary<uint, TextureAnimation> AnimationsByKey = new();
    private static readonly Dictionary<uint, (uint AnimKey, int Index)> FrameToAnimation = new();

    public static void Reset()
    {
        Materials.Clear();
        Textures.Clear();
        AnimationsByKey.Clear();
        FrameToAnimation.Clear();
    }

    public static int AnimationCount => AnimationsByKey.Count;

    public static void RegisterAnimations(IEnumerable<TextureAnimation> animations)
    {
        foreach (var anim in animations)
        {
            if (!AnimationsByKey.TryAdd(anim.Key, anim)) continue;
            for (var i = 0; i < anim.FrameHashes.Count; i++)
                FrameToAnimation.TryAdd(anim.FrameHashes[i], (anim.Key, i));
        }
    }

    public static void Add(string materialKey, string solidName, SolidObjectMaterial material, string effectName,
        Func<uint, Texture> resolveTexture)
    {
        var slots = new uint?[]
        {
            NonZero(material.DiffuseTextureHash), NonZero(material.NormalTextureHash),
            NonZero(material.SpecularTextureHash), NonZero(material.HeightTextureHash),
            NonZero(material.OpacityTextureHash),
        };

        // Drop slots that cannot be right. Diffuse (slot 0) is the material identity and stays.
        var dropped = new List<DroppedSlot>();
        for (var i = 1; i < slots.Length; i++)
        {
            if (slots[i] is not { } slotHash) continue;
            var tex = resolveTexture(slotHash);
            string reason = null;
            if (tex == null)
                reason = "no texture found";
            else if (RoleSuffix(tex.Name) is { } found && ExpectedSuffix[i] is { } expected && found != expected)
                reason = $"wrong role: _{found} texture in the {SlotNames[i]} slot";

            if (reason == null) continue;
            dropped.Add(new DroppedSlot(SlotNames[i], slotHash, tex?.Name, reason));
            slots[i] = null;
        }

        var effectId = (material as IEffectBasedMaterial)?.EffectId ?? 0;
        var sortKey = (material as ISortedMaterial)?.SortKey ?? 0;

        if (!Materials.TryGetValue(materialKey, out var record))
            Materials[materialKey] = record = new MaterialRecord { EffectName = effectName };

        var variant = record.Variants.FirstOrDefault(v =>
            v.EffectId == effectId && v.Flags == material.Flags && v.SortKey == sortKey && v.Slots.SequenceEqual(slots));
        if (variant == null)
        {
            variant = new Variant { EffectId = effectId, Flags = material.Flags, SortKey = sortKey, Slots = slots };
            record.Variants.Add(variant);
        }

        variant.Solids.Add(solidName);
        foreach (var d in dropped) variant.Dropped.Add(d);

        foreach (var hash in slots)
        {
            if (hash is not { } h) continue;
            EnsureTexture(h, resolveTexture);
            // The start frame of an animation must resolve in the textures table too.
            if (FrameToAnimation.TryGetValue(h, out var fa))
                EnsureTexture(StartFrame(fa.AnimKey), resolveTexture);
        }
    }

    private static void EnsureTexture(uint hash, Func<uint, Texture> resolveTexture)
    {
        if (!Textures.ContainsKey(hash))
            Textures[hash] = DescribeTexture(hash, resolveTexture(hash));
    }

    private static uint StartFrame(uint animKey) => AnimationsByKey[animKey].FrameHashes[0];

    private static uint? NonZero(uint? v) => v is > 0 ? v : null;

    // ---- output model ----

    private class ScrollRecord
    {
        public string Type { get; set; }
        public short? RawSpeedS { get; set; }
        public short? RawSpeedT { get; set; }
        public short? RawTimeStep { get; set; }
        public short? RawOffsetS { get; set; }
        public short? RawOffsetT { get; set; }
        public short? RawScaleS { get; set; }
        public short? RawScaleT { get; set; }
        public float? SpeedS { get; set; }
        public float? SpeedT { get; set; }
        public float? TimeStep { get; set; }
        public float? OffsetS { get; set; }
        public float? OffsetT { get; set; }
        public float? ScaleS { get; set; }
        public float? ScaleT { get; set; }
    }

    private class FrameRef
    {
        public string Animation { get; set; }
        public int Frame { get; set; }
    }

    private class TextureRecord
    {
        public string Name { get; set; }
        public uint? Width { get; set; }
        public uint? Height { get; set; }
        public string AlphaUsage { get; set; }
        public string AlphaBlend { get; set; }
        public string Tiling { get; set; }
        public string RenderFlags { get; set; }
        public ScrollRecord Scroll { get; set; }
        public FrameRef FrameSwap { get; set; }
    }

    private static TextureRecord DescribeTexture(uint hash, Texture tex)
    {
        var rec = new TextureRecord();
        if (FrameToAnimation.TryGetValue(hash, out var fa))
            rec.FrameSwap = new FrameRef { Animation = Hex(fa.AnimKey), Frame = fa.Index };

        if (tex == null) return rec;

        rec.Name = tex.Name;
        rec.Width = tex.Width;
        rec.Height = tex.Height;
        rec.AlphaUsage = tex.AlphaUsageType?.ToString();
        rec.AlphaBlend = tex.AlphaBlendType?.ToString();
        rec.Tiling = tex.TilableUV?.ToString();
        rec.RenderFlags = tex.RenderFlags?.ToString();

        // A smooth or snap scroll with speed 0 and 0 does not move, so it is no scroll.
        var zeroSpeed = (tex.ScrollSpeedS ?? 0) == 0 && (tex.ScrollSpeedT ?? 0) == 0;
        if (tex.ScrollType is { } st && st != TextureScrollType.None
            && (st == TextureScrollType.OffsetScale || !zeroSpeed))
        {
            var s = new ScrollRecord
            {
                Type = st.ToString(),
                RawSpeedS = tex.ScrollSpeedS, RawSpeedT = tex.ScrollSpeedT, RawTimeStep = tex.ScrollTimeStep,
                RawOffsetS = tex.OffsetS, RawOffsetT = tex.OffsetT, RawScaleS = tex.ScaleS, RawScaleT = tex.ScaleT,
            };

            if (st == TextureScrollType.OffsetScale)
            {
                s.OffsetS = tex.OffsetS / -1024f;
                s.OffsetT = tex.OffsetT / -1024f;
                s.ScaleS = tex.ScaleS / 256f;
                s.ScaleT = tex.ScaleT / 256f;
            }
            else
            {
                s.SpeedS = tex.ScrollSpeedS / 4096f;
                s.SpeedT = tex.ScrollSpeedT / 4096f;
                if (st == TextureScrollType.Snap) s.TimeStep = tex.ScrollTimeStep / 256f;
            }

            rec.Scroll = s;
        }

        return rec;
    }

    private class AnimationUsage
    {
        public string Slot { get; set; }
        public string Kind { get; set; } // "uv_scroll" or "frame_swap"
        public string ScrollType { get; set; }
        public string Animation { get; set; }
        public int? Frame { get; set; }
        public string StartFrame { get; set; }
        public string StartFrameName { get; set; }
    }

    private class MergeTarget
    {
        public string Material { get; set; }
        public int Variant { get; set; }
    }

    private class VariantOut
    {
        public int Id { get; set; }

        /// <summary>recommended, replace, distinct or animated.</summary>
        public string Role { get; set; }

        public int MappedSlots { get; set; }
        public Dictionary<string, string> Slots { get; set; }
        public string EffectId { get; set; }
        public string Flags { get; set; }
        public string SortKey { get; set; }

        /// <summary>Set on a "replace" variant: give it the slots of this variant of the same material.</summary>
        public int? ReplaceWith { get; set; }

        /// <summary>Set on an animated frame-swap variant: merge it into this variant of another material.</summary>
        public MergeTarget MergeInto { get; set; }

        /// <summary>What still differs from the target (flags, sortKey). Informational.</summary>
        public List<string> DiffersFromTarget { get; set; }

        public List<AnimationUsage> Animated { get; set; }

        /// <summary>Slots removed because no texture was found or the name role was wrong.</summary>
        public List<DroppedOut> DroppedSlots { get; set; }

        public int SolidCount { get; set; }
        public List<string> Solids { get; set; }
    }

    private class DroppedOut
    {
        public string Slot { get; set; }
        public string Texture { get; set; }
        public string Name { get; set; }
        public string Reason { get; set; }
    }

    private class MaterialOut
    {
        public string Effect { get; set; }

        /// <summary>The non-animated variant with the most mapped slots. Absent when every variant is animated.</summary>
        public int? RecommendedVariant { get; set; }

        public List<string> ConflictingSlots { get; set; }
        public List<VariantOut> Variants { get; set; }
    }

    private class AnimationOut
    {
        public string Name { get; set; }
        public int FrameCount { get; set; }
        public int Fps { get; set; }
        public string TimeBase { get; set; }
        public List<string> Frames { get; set; }
    }

    private static string Hex(uint v) => $"0x{v:X8}";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // ---- analysis ----

    private static bool IsSubset(Variant small, Variant big)
    {
        for (var i = 0; i < SlotNames.Length; i++)
            if (small.Slots[i] is { } h && big.Slots[i] != h)
                return false;
        return true;
    }

    private static List<string> Differences(Variant v, Variant target)
    {
        var list = new List<string>();
        if (v.Flags != target.Flags) list.Add("flags");
        if (v.SortKey != target.SortKey) list.Add("sortKey");
        return list.Count > 0 ? list : null;
    }

    private static uint? Canonical(uint? hash) =>
        hash is { } h && FrameToAnimation.TryGetValue(h, out var fa) ? StartFrame(fa.AnimKey) : hash;

    private static int FrameIndexOf(Variant v) =>
        v.Slots.Where(s => s != null && FrameToAnimation.ContainsKey(s.Value))
            .Select(s => FrameToAnimation[s!.Value].Index).DefaultIfEmpty(0).Min();

    private static void Analyze()
    {
        foreach (var record in Materials.Values)
        {
            record.Variants = record.Variants
                .OrderByDescending(v => v.Score)
                .ThenByDescending(v => v.Solids.Count)
                .ThenBy(v => v.SortKey)
                .ToList();

            for (var i = 0; i < record.Variants.Count; i++)
            {
                var v = record.Variants[i];
                v.Id = i;
                foreach (var hash in v.Slots)
                {
                    if (hash is not { } h || !Textures.TryGetValue(h, out var tex)) continue;
                    if (tex.Scroll != null) v.HasScroll = true;
                    if (tex.FrameSwap != null) v.HasFrameSwap = true;
                }
            }

            // Detail merge. Variants are sorted by mapped slots, so any superset comes earlier.
            var roots = new List<Variant>();
            foreach (var v in record.Variants.Where(v => !v.Animated))
            {
                var target = roots.FirstOrDefault(r => r.EffectId == v.EffectId && IsSubset(v, r));
                if (target != null)
                {
                    v.ReplaceWith = target.Id;
                    v.Differs = Differences(v, target);
                }
                else
                {
                    v.IsRoot = true;
                    roots.Add(v);
                }
            }
        }

        // Frame-swap merge across materials: same effect and slots once every frame is replaced by its
        // start frame. Flags and SortKey do not block the merge (they are reported instead).
        var groups = new Dictionary<string, List<(string Key, Variant V)>>();
        foreach (var (key, record) in Materials)
        foreach (var v in record.Variants.Where(v => v.HasFrameSwap))
        {
            var sig = $"{v.EffectId}|{record.EffectName}|" + string.Join(",", v.Slots.Select(Canonical));
            if (!groups.TryGetValue(sig, out var list)) groups[sig] = list = new();
            list.Add((key, v));
        }

        foreach (var list in groups.Values)
        {
            if (list.Select(m => m.Key).Distinct().Count() < 2) continue;
            var rep = list.OrderBy(m => FrameIndexOf(m.V)).ThenBy(m => m.Key, StringComparer.Ordinal).First();
            foreach (var m in list.Where(m => m.Key != rep.Key))
            {
                m.V.MergeMaterial = rep.Key;
                m.V.MergeVariant = rep.V.Id;
                m.V.Differs = Differences(m.V, rep.V);
            }
        }
    }

    /// <summary>Writes the dictionary. Returns a one-line summary, or null if no material was added.</summary>
    public static string Write(string path)
    {
        if (Materials.Count == 0) return null;

        Analyze();

        var materialsOut = new SortedDictionary<string, MaterialOut>(StringComparer.Ordinal);

        foreach (var (key, record) in Materials)
        {
            var conflicts = new List<string>();
            for (var i = 0; i < SlotNames.Length; i++)
            {
                var distinct = record.Variants.Select(v => v.Slots[i]).Where(s => s != null).Distinct().Count();
                if (distinct > 1) conflicts.Add(SlotNames[i]);
            }

            materialsOut[key] = new MaterialOut
            {
                Effect = record.EffectName,
                RecommendedVariant = record.Variants.FirstOrDefault(v => v.IsRoot)?.Id,
                ConflictingSlots = conflicts.Count > 0 ? conflicts : null,
                Variants = record.Variants.Select(v => BuildVariant(v, record)).ToList(),
            };
        }

        var animationsOut = AnimationsByKey.OrderBy(a => a.Key).ToDictionary(
            a => Hex(a.Key),
            a => new AnimationOut
            {
                Name = a.Value.Name,
                FrameCount = a.Value.FrameCount,
                Fps = a.Value.Fps,
                TimeBase = a.Value.TimeBase.ToString(),
                Frames = a.Value.FrameHashes.Select(Hex).ToList(),
            });

        var allVariants = Materials.Values.SelectMany(r => r.Variants).ToList();
        var summary = $"{Materials.Count} material(s), {allVariants.Count} variant(s), " +
                      $"{allVariants.Count(v => v.ReplaceWith != null)} replaceable by a more detailed variant, " +
                      $"{allVariants.Count(v => v.Animated)} animated (kept), " +
                      $"{allVariants.Count(v => v.MergeMaterial != null)} frame-swap merge(s) into the start frame, " +
                      $"{allVariants.Sum(v => v.Dropped.Count)} bad slot(s) dropped";

        var root = new
        {
            summary,
            materials = materialsOut,
            textures = Textures.ToDictionary(t => Hex(t.Key), t => t.Value),
            animations = animationsOut,
        };

        File.WriteAllText(path, JsonSerializer.Serialize(root, Options));
        return summary;
    }

    private static VariantOut BuildVariant(Variant v, MaterialRecord record)
    {
        var slots = new Dictionary<string, string>();
        var animated = new List<AnimationUsage>();

        for (var i = 0; i < SlotNames.Length; i++)
        {
            if (v.Slots[i] is not { } hash) continue;
            slots[SlotNames[i]] = Hex(hash);

            if (!Textures.TryGetValue(hash, out var tex)) continue;
            if (tex.Scroll != null)
                animated.Add(new AnimationUsage { Slot = SlotNames[i], Kind = "uv_scroll", ScrollType = tex.Scroll.Type });
            if (tex.FrameSwap != null)
            {
                var start = StartFrame(AnimationsByKey.Keys.First(k => Hex(k) == tex.FrameSwap.Animation));
                animated.Add(new AnimationUsage
                {
                    Slot = SlotNames[i],
                    Kind = "frame_swap",
                    Animation = tex.FrameSwap.Animation,
                    Frame = tex.FrameSwap.Frame,
                    StartFrame = Hex(start),
                    StartFrameName = Textures.TryGetValue(start, out var st) ? st.Name : null,
                });
            }
        }

        var role = v.Animated ? "animated" : v.ReplaceWith != null ? "replace"
            : v == record.Variants.First(x => x.IsRoot) ? "recommended" : "distinct";

        return new VariantOut
        {
            Id = v.Id,
            Role = role,
            MappedSlots = v.Score,
            Slots = slots,
            EffectId = $"0x{v.EffectId:X4}",
            Flags = Hex(v.Flags),
            SortKey = Hex(v.SortKey),
            ReplaceWith = v.ReplaceWith,
            MergeInto = v.MergeMaterial == null ? null : new MergeTarget { Material = v.MergeMaterial, Variant = v.MergeVariant!.Value },
            DiffersFromTarget = v.Differs,
            Animated = animated.Count > 0 ? animated : null,
            DroppedSlots = v.Dropped.Count == 0
                ? null
                : v.Dropped.OrderBy(d => d.Slot).ThenBy(d => d.Hash)
                    .Select(d => new DroppedOut { Slot = d.Slot, Texture = Hex(d.Hash), Name = d.Name, Reason = d.Reason })
                    .ToList(),
            SolidCount = v.Solids.Count,
            Solids = v.Solids.OrderBy(s => s, StringComparer.Ordinal).Take(MaxSolidsListed).ToList(),
        };
    }
}
