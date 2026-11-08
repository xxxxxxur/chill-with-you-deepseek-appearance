using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

public static class NativeShaderBundle
{
    const string Root = "Assets/DeepSeek";
    const string Native = Root + "/Native";
    const string ManifestAddress = "NativeMaterialManifest";

    [Serializable] class SourceCatalog { public SourceMaterial[] materials; public int[] faceOverlaySubmeshes; }
    [Serializable] class SourceMaterial { public string name, file; public float[] linearColor; }

    [Serializable] class Catalog
    {
        public int version = 1;
        public MaterialEntry[] materials;
        public RendererEntry[] renderers;
    }

    [Serializable] class MaterialEntry { public string name, texture, templateRenderer; public Color color; }
    [Serializable] class RendererEntry { public string name; public string[] materials; }

    [MenuItem("DeepSeek/Import Blender Data and Build Native Bundle")]
    public static void ReimportAndBuild()
    {
        DeepSeekExport.ImportModel();
        Build();
    }

    [MenuItem("DeepSeek/Build Native Game Shader Bundle")]
    public static void Build()
    {
        Directory.CreateDirectory(Native);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        var source = JsonUtility.FromJson<SourceCatalog>(File.ReadAllText(Root + "/Source/materials.json"));
        var input = AssetDatabase.LoadAssetAtPath<GameObject>(Root + "/Generated/Mod_Character.prefab");
        Require(input != null, "Import the Blender model before building the native shader bundle.");
        string prefabPath = Native + "/Mod_Character.prefab";
        string manifestPath = Native + "/NativeMaterials.bytes";
        var catalog = new Catalog();
        var paths = new List<string> { prefabPath, manifestPath };
        var addresses = new List<string> { "Mod_Character", ManifestAddress };

        const string overlayPath = Root + "/Shaders/FaceOverlay.shader";
        const string overlayAddress = "DeepSeekFaceOverlay";
        const string overlaySlotsAddress = "DeepSeekFaceOverlaySlots";
        string overlaySlotsPath = Native + "/FaceOverlaySlots.bytes";
        var face = input.GetComponentsInChildren<SkinnedMeshRenderer>().Single(r => r.name == "Face");
        var overlaySlots = source.faceOverlaySubmeshes ?? Array.Empty<int>();
        if (overlaySlots.Length > 0)
        {
            Require(overlaySlots.Distinct().Count() == overlaySlots.Length &&
                overlaySlots.All(slot => slot >= 0 && slot < face.sharedMesh.subMeshCount),
                "Invalid face overlay submeshes.");
            using (var writer = new BinaryWriter(File.Create(overlaySlotsPath)))
            {
                writer.Write(overlaySlots.Length);
                foreach (int slot in overlaySlots)
                    writer.Write(slot);
            }
            AssetDatabase.ImportAsset(overlaySlotsPath, ImportAssetOptions.ForceSynchronousImport);
            paths.Add(overlaySlotsPath);
            addresses.Add(overlaySlotsAddress);
            paths.Add(overlayPath);
            addresses.Add(overlayAddress);
        }

        catalog.materials = source.materials.Select(entry =>
        {
            string texture = string.Empty;
            if (!string.IsNullOrEmpty(entry.file))
            {
                string texturePath = Root + "/Textures/" + entry.file;
                Require(AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath) != null, "Missing texture " + texturePath);
                texture = "DeepSeekTexture/" + entry.name;
                paths.Add(texturePath);
                addresses.Add(texture);
            }

            var c = entry.linearColor;
            return new MaterialEntry
            {
                name = entry.name,
                texture = texture,
                color = new Color(c[0], c[1], c[2], c[3]).gamma,
                templateRenderer = OriginalRenderer(entry.name)
            };
        }).ToArray();

        var instance = (GameObject)PrefabUtility.InstantiatePrefab(input);
        try
        {
            PrefabUtility.UnpackPrefabInstance(instance, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            var renderers = instance.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var names = new HashSet<string>(catalog.materials.Select(m => m.name));
            catalog.renderers = renderers.Select(renderer =>
            {
                string[] materials = renderer.sharedMaterials.Select(m => m.name).ToArray();
                Require(materials.Length == renderer.sharedMesh.subMeshCount && materials.All(names.Contains),
                    "Missing palette entry for " + renderer.name);
                renderer.sharedMaterials = new Material[materials.Length];
                return new RendererEntry { name = renderer.name, materials = materials };
            }).ToArray();
            PrefabUtility.SaveAsPrefabAsset(instance, prefabPath);
        }
        finally
        {
            Object.DestroyImmediate(instance);
        }

        WriteManifest(manifestPath, catalog);
        AssetDatabase.ImportAsset(manifestPath, ImportAssetOptions.ForceSynchronousImport);
        AssetDatabase.SaveAssets();
        Require(!AssetDatabase.GetDependencies(prefabPath).Any(p => p.EndsWith(".mat") || p.EndsWith(".shader")),
            "The native prefab must not reference an editor shader or material.");

        string output = Path.GetFullPath("Build/NativeShader");
        Directory.CreateDirectory(output);
        var bundles = BuildPipeline.BuildAssetBundles(output, new[]
        {
            new AssetBundleBuild { assetBundleName = "assets", assetNames = paths.ToArray(), addressableNames = addresses.ToArray() }
        }, BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode, BuildTarget.StandaloneWindows64);
        Require(bundles != null, "Native AssetBundle build failed.");

        string file = Path.Combine(output, "assets");
        var bundle = AssetBundle.LoadFromFile(file);
        Require(bundle != null, "Cannot read the rebuilt AssetBundle.");
        try
        {
            var model = bundle.LoadAsset<GameObject>("Mod_Character");
            var manifest = bundle.LoadAsset<TextAsset>(ManifestAddress);
            Require(model != null && manifest != null, "Bundle is missing the prefab or material manifest.");
            Require(manifest.bytes.SequenceEqual(File.ReadAllBytes(manifestPath)), "The embedded manifest does not match its source.");
            var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            Require(renderers.Length > 0 && catalog.renderers.Length == renderers.Length, "Wrong mesh count.");
            foreach (var r in renderers)
            {
                var entry = catalog.renderers.Single(e => e.name == r.name);
                Require(r.sharedMesh.subMeshCount == entry.materials.Length && r.sharedMaterials.All(m => !m),
                    "Incorrect native material slots for " + r.name);
            }
            Require(catalog.materials.All(m => string.IsNullOrEmpty(m.texture) || bundle.LoadAsset<Texture2D>(m.texture)),
                "A custom base texture is missing.");
            int expectedShaders = overlaySlots.Length > 0 ? 1 : 0;
            Require(bundle.LoadAllAssets<Shader>().Length == expectedShaders, "Unexpected shaders in bundle.");
            if (expectedShaders > 0)
            {
                var shader = bundle.LoadAsset<Shader>(overlayAddress);
                var slots = bundle.LoadAsset<TextAsset>(overlaySlotsAddress);
                Require(shader && shader.passCount == 4 && slots && slots.bytes.SequenceEqual(File.ReadAllBytes(overlaySlotsPath)),
                    "Face overlay passes or slot metadata were not bundled.");
            }
            Debug.Log("NATIVE_BUNDLE_OK " + file + " " + new FileInfo(file).Length + " bytes, " +
                renderers.Length + " meshes, " + catalog.materials.Length + " palette entries, " + overlaySlots.Length + " face overlay submeshes");
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    static void WriteManifest(string path, Catalog catalog)
    {
        using var writer = new BinaryWriter(File.Create(path));
        writer.Write(catalog.version);
        writer.Write(catalog.materials.Length);
        foreach (var material in catalog.materials)
        {
            writer.Write(material.name);
            writer.Write(material.texture);
            writer.Write(material.templateRenderer);
            writer.Write(material.color.r);
            writer.Write(material.color.g);
            writer.Write(material.color.b);
            writer.Write(material.color.a);
        }

        writer.Write(catalog.renderers.Length);
        foreach (var renderer in catalog.renderers)
        {
            writer.Write(renderer.name);
            writer.Write(renderer.materials.Length);
            foreach (string slot in renderer.materials)
                writer.Write(slot);
        }
    }

    static string OriginalRenderer(string material)
    {
        switch (material)
        {
            case "Face":
            case "Eye":
            case "Eyebrow":
            case "BrowHair":
            case "EyelidAccent":
                return "Face";
            case "Body":
                return "Body";
            case "Hair":
                return "Hair1";
            default:
                return "Paka1";
        }
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}





