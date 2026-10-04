using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using Common.Scenery.Data;

namespace AssetDumper
{
    /// <summary>
    /// Writes the scenery data that can't be embedded in the exported scene
    /// files (instance flags, LOD keys, radius, bounding boxes) as two TSV
    /// files in the output directory: scenery_infos.tsv (one row per
    /// SceneryInfo) and scenery_instances.tsv (one row per SceneryInstance).
    /// Plain tab separated text so it reads fine in a text editor or a
    /// spreadsheet. Kept in its own class/file, same as
    /// UndercoverLayerClassifier, rather than growing ExportBundleCommand.
    ///
    /// The named override groups (scenery_override_infos and scenery_groups)
    /// are NOT written here: they are stored in the region file, which
    /// AssetDumper does not read. The stream repo's nfs_stream_scenery.py
    /// writes them and joins them to this file on Section + Instance.
    ///
    /// Join key to the exported scene: the Instance column is the real
    /// on-disk SceneryInstance position (rawInstanceIndex in
    /// ExportScenerySection), which is also the number at the end of the
    /// exported node id "scene_ScenerySection_{Section}_node_{Instance}".
    /// Extra LOD nodes (--export-lods) add "_lod{slot}" to that id.
    /// </summary>
    public static class SceneryManifestWriter
    {
        private const string InfosFileName = "scenery_infos.tsv";
        private const string InstancesFileName = "scenery_instances.tsv";

        public static void Write(string outputDir, IEnumerable<ScenerySection> scenerySections)
        {
            var infos = new StringBuilder();
            var instances = new StringBuilder();

            infos.AppendLine(string.Join("\t", "Section", "Info", "Name", "SolidKey1", "SolidKey2", "SolidKey3",
                "SolidKey4", "Radius", "HierarchyHash", "Flags"));
            instances.AppendLine(string.Join("\t", "Section", "Instance", "Guid", "Info", "Name", "Flags", "FlagNames",
                "BBoxMinX", "BBoxMinY", "BBoxMinZ", "BBoxMaxX", "BBoxMaxY", "BBoxMaxZ"));

            foreach (var scenerySection in scenerySections.OrderBy(s => s.SectionNumber))
            {
                for (var infoIndex = 0; infoIndex < scenerySection.Infos.Count; infoIndex++)
                {
                    var info = scenerySection.Infos[infoIndex];
                    // Games without LOD keys in their reader only have SolidKey
                    var solidKeys = info.SolidKeys ?? new[] { info.SolidKey, 0u, 0u, 0u };

                    infos.AppendLine(string.Join("\t",
                        scenerySection.SectionNumber,
                        infoIndex,
                        Clean(info.Name),
                        Hex(solidKeys[0]), Hex(solidKeys[1]), Hex(solidKeys[2]), Hex(solidKeys[3]),
                        Float(info.Radius),
                        Hex(info.HierarchyNameHash),
                        Hex(info.Flags)));
                }

                for (var instanceIndex = 0; instanceIndex < scenerySection.Instances.Count; instanceIndex++)
                {
                    var instance = scenerySection.Instances[instanceIndex];
                    var info = scenerySection.Infos[instance.InfoIndex];

                    instances.AppendLine(string.Join("\t",
                        scenerySection.SectionNumber,
                        instanceIndex,
                        Hex(instance.SceneryGuid),
                        instance.InfoIndex,
                        Clean(info.Name),
                        Hex((uint)instance.Flags),
                        instance.Flags.ToString().Replace(", ", "|"),
                        Float(instance.BBoxMin.X), Float(instance.BBoxMin.Y), Float(instance.BBoxMin.Z),
                        Float(instance.BBoxMax.X), Float(instance.BBoxMax.Y), Float(instance.BBoxMax.Z)));
                }
            }

            File.WriteAllText(Path.Combine(outputDir, InfosFileName), infos.ToString());
            File.WriteAllText(Path.Combine(outputDir, InstancesFileName), instances.ToString());
        }

        private static string Hex(uint value) => $"0x{value:X8}";

        private static string Float(float value) => value.ToString(CultureInfo.InvariantCulture);

        // A tab or a line break inside a name would break the columns
        private static string Clean(string value) =>
            (value ?? "").Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
    }
}
