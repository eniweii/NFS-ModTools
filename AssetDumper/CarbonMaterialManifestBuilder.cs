using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Common.Geometry;
using Common.Textures.Data;

namespace AssetDumper
{
    /// <summary>
    /// Builds one structured manifest entry per Carbon material: its role-mapped
    /// texture slots (diffuse/normal/specular/height/opacity) plus each texture's
    /// real render attributes (alpha usage/blend, tiling, scroll, flags). Replaces
    /// the old materialtextureusage.txt scratch dump for Carbon specifically.
    /// Kept in its own class/file, same as UndercoverLayerClassifier is for
    /// Undercover, rather than growing ExportBundleCommand - Carbon only for now;
    /// Undercover/ProStreet have their own slot-resolution logic and aren't wired
    /// into this yet.
    /// </summary>
    public static class CarbonMaterialManifestBuilder
    {
        public class TextureEntry
        {
            public string Role { get; set; }
            public string Hash { get; set; }
            public string Name { get; set; }
            public string AlphaUsageType { get; set; }
            public string AlphaBlendType { get; set; }
            public string TilableUV { get; set; }
            public string RenderFlags { get; set; }
            public string ScrollType { get; set; }
            public short? ScrollSpeedS { get; set; }
            public short? ScrollSpeedT { get; set; }
        }

        public class MaterialEntry
        {
            public string Solid { get; set; }
            public string Material { get; set; }
            public string EffectId { get; set; }
            public string SortKey { get; set; }
            public List<TextureEntry> Textures { get; set; } = new();
        }

        private static TextureEntry Describe(string role, uint? hash, Func<uint, Texture> resolveTexture)
        {
            if (hash is not { } h || h == 0) return null;

            var tex = resolveTexture(h);
            return new TextureEntry
            {
                Role = role,
                Hash = $"0x{h:X8}",
                Name = tex?.Name,
                AlphaUsageType = tex?.AlphaUsageType?.ToString(),
                AlphaBlendType = tex?.AlphaBlendType?.ToString(),
                TilableUV = tex?.TilableUV?.ToString(),
                RenderFlags = tex?.RenderFlags?.ToString(),
                ScrollType = tex?.ScrollType?.ToString(),
                ScrollSpeedS = tex?.ScrollSpeedS,
                ScrollSpeedT = tex?.ScrollSpeedT,
            };
        }

        public static MaterialEntry BuildEntry(string solidName, string materialExportName, CarbonMaterial material,
            Func<uint, Texture> resolveTexture)
        {
            var entry = new MaterialEntry
            {
                Solid = solidName,
                Material = materialExportName,
                EffectId = $"0x{material.EffectId:X4}",
                SortKey = $"0x{material.SortKey:X8}",
            };

            void Add(string role, uint? hash)
            {
                var tex = Describe(role, hash, resolveTexture);
                if (tex != null) entry.Textures.Add(tex);
            }

            Add("diffuse", material.DiffuseTextureHash);
            Add("normal", material.NormalTextureHash);
            Add("specular", material.SpecularTextureHash);
            Add("height", material.HeightTextureHash);
            Add("opacity", material.OpacityTextureHash);

            return entry;
        }

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = false };

        public static void Append(string outputPath, MaterialEntry entry) =>
            File.AppendAllText(outputPath, JsonSerializer.Serialize(entry, Options) + Environment.NewLine);
    }
}
