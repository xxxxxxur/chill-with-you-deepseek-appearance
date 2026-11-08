using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Object = UnityEngine.Object;

public static class DeepSeekExport
{
    const string Root = "Assets/DeepSeek";
    const string Source = Root + "/Source";
    const string Output = Root + "/Generated";
    const string Prefab = Output + "/Mod_Character.prefab";
    const string PreviewScene = Output + "/MaterialPreview.unity";
    const string PipelinePath = "Packages/com.unity.render-pipelines.universal";
    const string ShaderName = "Universal Render Pipeline/Lit";
    static readonly string BundleFolder = Path.GetFullPath("Build/ChillWithAnyone");
    static readonly string PreviewFile = Path.GetFullPath("Build/material-preview.png");

    [Serializable] class SourceCatalog
    {
        public MaterialSource[] materials;
        public string[] meshes;
        public string[] faceBlendShapes;
    }

    [Serializable] class MaterialSource
    {
        public string name;
        public string file;
        public float[] linearColor;
    }

    [Serializable] class Skeleton
    {
        public BoneSource[] bones;
    }

    [Serializable] class BoneSource
    {
        public string name;
        public string parent;
        public float[] bind;
    }

    [MenuItem("DeepSeek/Import Blender Data and Build")]
    public static void Run()
    {
        ImportModel();
        CreatePreviewScene();
        BuildBundle();
        if (SystemInfo.graphicsDeviceType != GraphicsDeviceType.Null)
            RenderPreview();
    }

    internal static void ImportModel()
    {
        Directory.CreateDirectory(Output);
        AssetDatabase.Refresh(ImportAssetOptions.ForceSynchronousImport);
        PlayerSettings.colorSpace = ColorSpace.Linear;

        var catalog = JsonUtility.FromJson<SourceCatalog>(File.ReadAllText(Source + "/materials.json"));
        var skeleton = JsonUtility.FromJson<Skeleton>(File.ReadAllText(Source + "/skeleton.json"));
        var bindMatrices = new Dictionary<string, Matrix4x4>();
        var bones = new Dictionary<string, Transform>();

        var shader = AssetDatabase.LoadAssetAtPath<Shader>(PipelinePath + "/Shaders/Lit.shader");
        Require(shader != null && shader.name == ShaderName, "Unity URP Lit shader is unavailable");
        var materials = catalog.materials.ToDictionary(item => item.name, item => CreateMaterial(item, shader));
        var model = new GameObject("Mod_Character");
        try
        {
            foreach (var source in skeleton.bones)
            {
                var world = new Matrix4x4();
                for (int row = 0; row < 4; row++)
                for (int column = 0; column < 4; column++)
                    world[row, column] = source.bind[row * 4 + column];

                bool root = string.IsNullOrEmpty(source.parent);
                var transform = new GameObject(source.name).transform;
                transform.SetParent(root ? model.transform : bones[source.parent], false);
                var local = root ? world : bindMatrices[source.parent].inverse * world;
                transform.localPosition = local.GetColumn(3);
                transform.localRotation = local.rotation;
                transform.localScale = local.lossyScale;
                bindMatrices.Add(source.name, world);
                bones.Add(source.name, transform);
                Require(MatrixError(transform.localToWorldMatrix, world) < 0.00005f, source.name + " bind transform mismatch");
            }

            foreach (string name in catalog.meshes)
                ImportMesh(name, model.transform, bones, bindMatrices, materials);

            ValidateModel(model);
            PrefabUtility.SaveAsPrefabAsset(model, Prefab);
            AssetDatabase.SaveAssets();
        }
        finally
        {
            Object.DestroyImmediate(model);
        }
    }

    static Material CreateMaterial(MaterialSource source, Shader shader)
    {
        var material = new Material(shader) { name = source.name };
        var c = source.linearColor;
        material.SetColor("_BaseColor", new Color(c[0], c[1], c[2], c[3]).gamma);
        if (!string.IsNullOrEmpty(source.file))
        {
            string path = Root + "/Textures/" + source.file;
            var importer = (TextureImporter)AssetImporter.GetAtPath(path);
            Require(importer != null, "Missing texture " + path);
            importer.sRGBTexture = true;
            importer.alphaSource = TextureImporterAlphaSource.None;
            importer.maxTextureSize = 4096;
            importer.textureCompression = TextureImporterCompression.CompressedHQ;
            importer.anisoLevel = 4;
            importer.SaveAndReimport();
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
            Require(texture != null, "Cannot import " + path);
            material.SetTexture("_BaseMap", texture);
        }
        AssetDatabase.CreateAsset(material, Output + "/" + source.name + ".mat");
        return material;
    }

    static void ImportMesh(string name, Transform parent, Dictionary<string, Transform> bones,
        Dictionary<string, Matrix4x4> bindMatrices, Dictionary<string, Material> materials)
    {
        using var reader = new BinaryReader(File.OpenRead(Source + "/" + name + ".bytes"));
        Require(reader.ReadInt32() == 1, "Unknown mesh data version");
        int boneCount = reader.ReadInt32();
        var meshBones = new Transform[boneCount];
        var bindposes = new Matrix4x4[boneCount];
        for (int index = 0; index < boneCount; index++)
        {
            string boneName = ReadString(reader);
            meshBones[index] = bones[boneName];
            bindposes[index] = bindMatrices[boneName].inverse;
        }

        int count = reader.ReadInt32();
        var positions = new Vector3[count];
        var normals = new Vector3[count];
        var uv = new Vector2[count];
        var weights = new BoneWeight[count];
        for (int index = 0; index < count; index++)
        {
            positions[index] = ReadVector(reader);
            normals[index] = ReadVector(reader);
            uv[index] = new Vector2(reader.ReadSingle(), reader.ReadSingle());
            weights[index] = new BoneWeight
            {
                boneIndex0 = reader.ReadInt32(), boneIndex1 = reader.ReadInt32(),
                boneIndex2 = reader.ReadInt32(), boneIndex3 = reader.ReadInt32(),
                weight0 = reader.ReadSingle(), weight1 = reader.ReadSingle(),
                weight2 = reader.ReadSingle(), weight3 = reader.ReadSingle()
            };
        }

        var mesh = new Mesh
        {
            name = name,
            indexFormat = count > 65535 ? IndexFormat.UInt32 : IndexFormat.UInt16,
            vertices = positions, normals = normals, uv = uv, boneWeights = weights, bindposes = bindposes
        };

        int submeshCount = reader.ReadInt32();
        mesh.subMeshCount = submeshCount;
        var rendererMaterials = new Material[submeshCount];
        for (int index = 0; index < submeshCount; index++)
        {
            rendererMaterials[index] = materials[ReadString(reader)];
            int[] triangles = new int[reader.ReadInt32()];
            for (int i = 0; i < triangles.Length; i++)
                triangles[i] = reader.ReadInt32();
            mesh.SetTriangles(triangles, index, false);
        }

        int keyCount = reader.ReadInt32();
        for (int index = 0; index < keyCount; index++)
        {
            string keyName = ReadString(reader);
            var deltas = new Vector3[count];
            for (int i = 0; i < count; i++)
                deltas[i] = ReadVector(reader);
            mesh.AddBlendShapeFrame(keyName, 100, deltas, null, null);
        }
        Require(reader.BaseStream.Position == reader.BaseStream.Length, "Unread mesh data: " + name);
        mesh.RecalculateBounds();
        mesh.RecalculateTangents();
        AssetDatabase.CreateAsset(mesh, Output + "/" + name + ".asset");

        var part = new GameObject(name);
        part.transform.SetParent(parent, false);
        var renderer = part.AddComponent<SkinnedMeshRenderer>();
        renderer.sharedMesh = mesh;
        renderer.sharedMaterials = rendererMaterials;
        renderer.bones = meshBones;
        renderer.rootBone = bones["Character_Hips"];
        renderer.localBounds = mesh.bounds;
    }

    static void CreatePreviewScene()
    {
        var rendererData = ScriptableObject.CreateInstance<UniversalRendererData>();
        ResourceReloader.ReloadAllNullIn(rendererData, PipelinePath);
        AssetDatabase.CreateAsset(rendererData, Output + "/PreviewRenderer.asset");
        var pipeline = UniversalRenderPipelineAsset.Create(rendererData);
        AssetDatabase.CreateAsset(pipeline, Output + "/PreviewPipeline.asset");
        GraphicsSettings.defaultRenderPipeline = pipeline;
        QualitySettings.renderPipeline = pipeline;

        var scene = EditorSceneManager.NewScene(NewSceneSetup.EmptyScene, NewSceneMode.Single);
        PrefabUtility.InstantiatePrefab(AssetDatabase.LoadAssetAtPath<GameObject>(Prefab), scene);

        var camera = new GameObject("Preview Camera").AddComponent<Camera>();
        camera.transform.position = new Vector3(1.25f, 1.18f, 4.4f);
        camera.transform.LookAt(new Vector3(0, 0.87f, 0));
        camera.orthographic = true;
        camera.orthographicSize = 0.98f;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.19f, 0.21f, 0.25f);
        camera.nearClipPlane = 0.03f;
        camera.farClipPlane = 20;

        CreateLight("Key", new Vector3(-0.4f, -0.55f, -1), 0.82f, new Color(1f, 0.97f, 0.94f));
        CreateLight("Fill", new Vector3(0.7f, -0.18f, -1), 0.29f, new Color(0.85f, 0.93f, 1f));
        RenderSettings.ambientMode = AmbientMode.Flat;
        RenderSettings.ambientLight = new Color(0.36f, 0.38f, 0.43f);
        EditorSceneManager.SaveScene(scene, PreviewScene);
        AssetDatabase.SaveAssets();
    }

    static void CreateLight(string name, Vector3 direction, float intensity, Color color)
    {
        var light = new GameObject(name).AddComponent<Light>();
        light.type = LightType.Directional;
        light.intensity = intensity;
        light.color = color;
        light.transform.rotation = Quaternion.LookRotation(direction);
    }

    [MenuItem("DeepSeek/Build Windows AssetBundle")]
    public static void BuildBundle()
    {
        Directory.CreateDirectory(BundleFolder);
        var entries = new[]
        {
            new AssetBundleBuild
            {
                assetBundleName = "assets",
                assetNames = new[] { Prefab },
                addressableNames = new[] { "Mod_Character" }
            }
        };
        var manifest = BuildPipeline.BuildAssetBundles(BundleFolder, entries,
            BuildAssetBundleOptions.ChunkBasedCompression | BuildAssetBundleOptions.StrictMode,
            BuildTarget.StandaloneWindows64);
        Require(manifest != null, "AssetBundle build failed");

        string path = Path.Combine(BundleFolder, "assets");
        var bundle = AssetBundle.LoadFromFile(path);
        Require(bundle != null, "Cannot load generated bundle");
        try
        {
            var model = bundle.LoadAsset<GameObject>("Mod_Character");
            Require(model != null, "Mod_Character address is missing");
            ValidateModel(model);
            Debug.Log("DEEPSEEK_BUNDLE_OK " + path + " (" + new FileInfo(path).Length + " bytes)");
        }
        finally
        {
            bundle.Unload(true);
        }
    }

    [MenuItem("DeepSeek/Render Material Preview")]
    public static void RenderPreview()
    {
        EditorSceneManager.OpenScene(PreviewScene);
        var camera = Object.FindObjectOfType<Camera>();
        Require(camera != null, "Missing preview camera");
        var target = RenderTexture.GetTemporary(900, 1120, 24, RenderTextureFormat.ARGB32);
        var previous = RenderTexture.active;
        var capture = new Texture2D(target.width, target.height, TextureFormat.RGB24, false);
        try
        {
            var request = new UniversalRenderPipeline.SingleCameraRequest { destination = target };
            Require(RenderPipeline.SupportsRenderRequest(camera, request), "URP preview render request is unsupported");
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderPipeline.SubmitRenderRequest(camera, request);
            RenderTexture.active = target;
            capture.ReadPixels(new Rect(0, 0, target.width, target.height), 0, 0);
            capture.Apply();
            Directory.CreateDirectory(Path.GetDirectoryName(PreviewFile));
            File.WriteAllBytes(PreviewFile, capture.EncodeToPNG());
            Debug.Log("DEEPSEEK_PREVIEW_OK " + PreviewFile);
        }
        finally
        {
            RenderTexture.active = previous;
            RenderTexture.ReleaseTemporary(target);
            Object.DestroyImmediate(capture);
        }
    }

    static void ValidateModel(GameObject model)
    {
        var renderers = model.GetComponentsInChildren<SkinnedMeshRenderer>();
        var face = renderers.Single(renderer => renderer.name == "Face");
        var catalog = JsonUtility.FromJson<SourceCatalog>(File.ReadAllText(Source + "/materials.json"));
        Require(renderers.Select(r => r.name).OrderBy(name => name).SequenceEqual(catalog.meshes.OrderBy(name => name)),
            "Skinned renderers do not match the export catalog");
        var meshFace = face.sharedMesh;
        var actual = Enumerable.Range(0, meshFace.blendShapeCount).Select(meshFace.GetBlendShapeName);
        Require(catalog.faceBlendShapes.Length > 0 && actual.SequenceEqual(catalog.faceBlendShapes),
            "Face blend shapes must match the game channels in their original order");

        foreach (var renderer in renderers)
        {
            var mesh = renderer.sharedMesh;
            Require(mesh != null && mesh.vertexCount > 0, "Missing mesh for " + renderer.name);
            Require(renderer.rootBone.name == "Character_Hips", "Wrong root bone");
            var bones = renderer.bones;
            var bindposes = mesh.bindposes;
            Require(bones.Length == bindposes.Length, "Skin data is incomplete");
            for (int i = 0; i < bones.Length; i++)
            {
                Require(bones[i] && bones[i].name.StartsWith("Character_", StringComparison.Ordinal), "Unknown game bone");
                Require(MatrixError(bones[i].localToWorldMatrix * bindposes[i], renderer.localToWorldMatrix) < 0.0001f,
                    "Mesh bind pose mismatch: " + renderer.name + "/" + bones[i].name);
            }
            Require(renderer.sharedMaterials.Length == mesh.subMeshCount, "Missing material slots");
            Require(renderer.sharedMaterials.All(m => m && m.shader && m.shader.name == ShaderName),
                "Material does not use game URP Lit");
        }
        Debug.Log("DEEPSEEK_MODEL_OK " + renderers.Sum(r => r.sharedMesh.vertexCount) + " vertices, " +
            renderers.SelectMany(r => r.sharedMaterials).Distinct().Count() + " Lit materials");
    }

    static Vector3 ReadVector(BinaryReader reader) =>
        new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());

    static string ReadString(BinaryReader reader) => Encoding.UTF8.GetString(reader.ReadBytes(reader.ReadInt32()));

    static float MatrixError(Matrix4x4 a, Matrix4x4 b)
    {
        float error = 0;
        for (int i = 0; i < 16; i++)
            error = Mathf.Max(error, Mathf.Abs(a[i] - b[i]));
        return error;
    }

    static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidDataException(message);
    }
}




